// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// One-time importer that copies timestamps from any historical shape of the legacy
/// <c>introskipper.db</c> into the current database. The legacy file is opened
/// read-only and never modified, so downgrading to a pre-v2 plugin keeps working.
/// Shape detection is driven purely by <c>sqlite_master</c>/<c>pragma_table_info</c>
/// (never migration history), because the pre-v2 in-place repair era produced
/// databases whose columns and history do not always agree.
/// </summary>
internal static partial class LegacyDatabaseImporter
{
    /// <summary>
    /// Imports segments, season states and per-item analysis records from the legacy
    /// database into <paramref name="newDb"/>.
    /// The caller owns the surrounding transaction and the <see cref="DbImportRecord"/> marker;
    /// this method only reads the legacy file and stages/saves rows on the new context.
    /// </summary>
    /// <param name="newDb">Context of the current database.</param>
    /// <param name="legacyDbPath">Path of the legacy database file (must exist).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The marker row carrying the import counts and shape notes; the caller
    /// stamps the time and source-file flag before saving it.</returns>
    internal static async Task<DbImportRecord> ImportAsync(
        IntroSkipperDbContext newDb,
        string legacyDbPath,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Mode=ReadOnly (not Immutable=1) reads through a live -wal left behind by the old
        // plugin; read-only connections never checkpoint or truncate, so the .db and -wal
        // bytes stay untouched. Pooling=False releases the file handle on dispose.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = legacyDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var legacy = new SqliteConnection(connectionString);
        await legacy.OpenAsync(cancellationToken).ConfigureAwait(false);

        var (segmentsImported, segmentsSkipped, segmentNotes) =
            await ImportSegmentsAsync(newDb, legacy, logger, cancellationToken).ConfigureAwait(false);
        var (statesImported, stateNotes) =
            await ImportSeasonStatesAsync(newDb, legacy, logger, cancellationToken).ConfigureAwait(false);

        return new DbImportRecord
        {
            SegmentsImported = segmentsImported,
            SegmentsSkipped = segmentsSkipped,
            SeasonStatesImported = statesImported,
            Notes = $"{segmentNotes}; {stateNotes}"
        };
    }

    private static async Task<(int Imported, int Skipped, string Notes)> ImportSegmentsAsync(
        IntroSkipperDbContext newDb,
        SqliteConnection legacy,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!TableExists(legacy, "DbSegment"))
        {
            return (0, 0, "segments: no DbSegment table");
        }

        var columns = GetColumns(legacy, "DbSegment");
        if (!columns.Contains("ItemId") || !columns.Contains("Start") || !columns.Contains("End") || !columns.Contains("Type"))
        {
            return (0, 0, "segments: DbSegment table missing core columns");
        }

        var hasUser = columns.Contains("IsUserProvided");
        var hasHash = columns.Contains("ConfigHash");

        // Rows already present in the new database. Import and marker commit in one
        // transaction, so a crash never leaves partial rows; but a failed import is
        // swallowed and retried at the next start, and in between the plugin has run
        // (analysis, user edits) against the marker-less new file. The retry must not
        // contradict what that window recorded: `seen` blocks exact duplicates,
        // `blockers` holds the human intent (tombstones and active user rows) that
        // gates automatic legacy rows exactly like analysis writes, and
        // `occupantsByQuad` lets an exact collision with a window-era automatic row
        // preserve a legacy row's user provenance by promotion.
        var seen = new HashSet<(Guid ItemId, AnalysisMode Type, long StartTicks, long EndTicks)>();
        var blockers = new Dictionary<(Guid ItemId, AnalysisMode Type), List<(long StartTicks, long EndTicks)>>();
        var occupantsByQuad = new Dictionary<(Guid ItemId, AnalysisMode Type, long StartTicks, long EndTicks), Guid>();
        var existing = await newDb.Segments
            .AsNoTracking()
            .Select(s => new { s.Id, s.ItemId, s.Type, s.StartTicks, s.EndTicks, s.State, s.Source })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in existing)
        {
            seen.Add((row.ItemId, row.Type, row.StartTicks, row.EndTicks));
            if (row.State == SegmentState.Suppressed || row.Source == SegmentSource.User)
            {
                if (!blockers.TryGetValue((row.ItemId, row.Type), out var ranges))
                {
                    ranges = [];
                    blockers[(row.ItemId, row.Type)] = ranges;
                }

                ranges.Add((row.StartTicks, row.EndTicks));
            }
            else
            {
                occupantsByQuad[(row.ItemId, row.Type, row.StartTicks, row.EndTicks)] = row.Id;
            }
        }

        using var command = legacy.CreateCommand();

        // User rows first so they win the exact-duplicate dedupe below. The `= 1`
        // normalization matters because repair-era files can hold non-integer values in
        // the column, and SQLite orders by storage class first (TEXT above INTEGER under
        // DESC) — a garbage-flagged duplicate must not outrank a genuine user row.
#pragma warning disable CA2100 // Interpolation only splices compile-time literals selected by column presence.
        command.CommandText =
            $"""
            SELECT "ItemId", "Start", "End", "Type"
                {(hasUser ? ", \"IsUserProvided\"" : string.Empty)}
                {(hasHash ? ", \"ConfigHash\"" : string.Empty)}
            FROM "DbSegment"
            {(hasUser ? "ORDER BY (\"IsUserProvided\" = 1) DESC" : string.Empty)}
            """;
#pragma warning restore CA2100

        var imported = 0;
        var skipped = 0;
        var pending = new List<DbSegment>();
        var promotions = new HashSet<Guid>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var userOrdinal = hasUser ? reader.GetOrdinal("IsUserProvided") : -1;
            var hashOrdinal = hasHash ? reader.GetOrdinal("ConfigHash") : -1;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var startValue = reader.GetValue(1);
                var endValue = reader.GetValue(2);
                var typeValue = reader.GetValue(3);
                if (!TryReadGuid(reader.GetValue(0), out var itemId)
                    || !TryToDouble(startValue, out var startSeconds)
                    || !TryToDouble(endValue, out var endSeconds)
                    || !TryToInt32(typeValue, out var type))
                {
                    skipped++;
                    continue;
                }

                // IsSupported, not Enum.IsDefined: a stored mode without a
                // ModeToSegmentType entry would throw in SegmentDtoFactory on every
                // later mirror sync and provider read for the item.
                if (!AnalysisHelpers.IsSupported((AnalysisMode)type)
                    || !TickConversions.TryFromSecondsRange(startSeconds, endSeconds, out var startTicks, out var endTicks))
                {
                    skipped++;
                    continue;
                }

                var mode = (AnalysisMode)type;
                var userValue = hasUser ? reader.GetValue(userOrdinal) : null;
                var source = TryToInt64(userValue, out var userFlag) && userFlag != 0
                    ? SegmentSource.User
                    : SegmentSource.Unknown;

                // Automatic legacy rows obey the same admission rule as analysis writes:
                // they must not contradict the human intent (tombstones, user rows) the
                // retry window recorded. User rows are admitted unconditionally, as at
                // every other write door.
                if (source != SegmentSource.User
                    && blockers.TryGetValue((itemId, mode), out var ranges)
                    && ranges.Any(r => AutoSegmentAdmissionPolicy.Overlaps(startTicks, endTicks, r.StartTicks, r.EndTicks)))
                {
                    skipped++;
                    continue;
                }

                if (!seen.Add((itemId, mode, startTicks, endTicks)))
                {
                    // The exact range already exists. When the legacy row is
                    // user-provided and the occupant is an automatic row from the retry
                    // window, the user's provenance must survive: promote the occupant
                    // instead of silently dropping the flag (a tombstone occupant is
                    // newer human intent and wins as-is).
                    if (source == SegmentSource.User
                        && occupantsByQuad.TryGetValue((itemId, mode, startTicks, endTicks), out var occupantId))
                    {
                        promotions.Add(occupantId);
                    }

                    skipped++;
                    continue;
                }

                var configHash = ReadOptionalString(reader, hashOrdinal);

                pending.Add(new DbSegment(itemId, mode, startTicks, endTicks, source, configHash));
                imported++;

                if (pending.Count >= IntroSkipperDbContext.SaveBatchSize)
                {
                    await SaveBatchAsync(newDb, newDb.Segments, pending, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await SaveBatchAsync(newDb, newDb.Segments, pending, cancellationToken).ConfigureAwait(false);

        if (promotions.Count > 0)
        {
            var occupantIds = promotions.ToArray();
            var occupants = await newDb.Segments
                .Where(s => EF.Parameter(occupantIds).Contains(s.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var occupant in occupants)
            {
                occupant.PromoteToUser();
            }

            await newDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            newDb.ChangeTracker.Clear();
        }

        LogSegmentsImported(logger, imported, skipped);
        return (imported, skipped, $"segments: {(hasUser ? "+IsUserProvided" : "-IsUserProvided")} {(hasHash ? "+ConfigHash" : "-ConfigHash")}");
    }

    private static async Task<(int Imported, string Notes)> ImportSeasonStatesAsync(
        IntroSkipperDbContext newDb,
        SqliteConnection legacy,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var table = TableExists(legacy, "DbSeasonState") ? "DbSeasonState"
            : TableExists(legacy, "DbSeasonInfo") ? "DbSeasonInfo"
            : null;
        if (table is null)
        {
            return (0, "seasons: no season table");
        }

        var columns = GetColumns(legacy, table);
        if (!columns.Contains("SeasonId") || !columns.Contains("Type") || !columns.Contains("Action") || !columns.Contains("EpisodeIds"))
        {
            return (0, $"seasons: {table} table missing core columns");
        }

        var hasHash = columns.Contains("ConfigHash");
        var hasSettled = columns.Contains("SettledReanalysisEpisodeIds");

        // Same retry-against-a-populated-database case as the segment import: rows written
        // since the swallowed attempt win, keyed by (SeasonId, Type) for season state and
        // by (ItemId, Type) for analysis records.
        var existingKeys = new HashSet<(Guid SeasonId, AnalysisMode Type)>();
        var existing = await newDb.SeasonStates
            .AsNoTracking()
            .Select(s => new { s.SeasonId, s.Type })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in existing)
        {
            existingKeys.Add((row.SeasonId, row.Type));
        }

        var analyzedKeys = new HashSet<(Guid ItemId, AnalysisMode Type)>();
        var existingAnalyzed = await newDb.AnalyzedItems
            .AsNoTracking()
            .Select(a => new { a.ItemId, a.Type })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in existingAnalyzed)
        {
            analyzedKeys.Add((row.ItemId, row.Type));
        }

        using var command = legacy.CreateCommand();
#pragma warning disable CA2100 // Interpolation only splices compile-time literals selected by table/column presence.
        command.CommandText =
            $"""
            SELECT "SeasonId", "Type", "Action", "EpisodeIds"
                {(hasHash ? ", \"ConfigHash\"" : string.Empty)}
                {(hasSettled ? ", \"SettledReanalysisEpisodeIds\"" : string.Empty)}
            FROM "{table}"
            """;
#pragma warning restore CA2100

        var imported = 0;
        var analyzedImported = 0;
        var states = new List<DbSeasonState>();
        var analyzed = new List<DbAnalyzedItem>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var hashOrdinal = hasHash ? reader.GetOrdinal("ConfigHash") : -1;
            var settledOrdinal = hasSettled ? reader.GetOrdinal("SettledReanalysisEpisodeIds") : -1;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var typeValue = reader.GetValue(1);
                if (!TryReadGuid(reader.GetValue(0), out var seasonId)
                    || !TryToInt32(typeValue, out var type)
                    || !Enum.IsDefined((AnalysisMode)type))
                {
                    continue;
                }

                var mode = (AnalysisMode)type;
                var actionValue = reader.GetValue(2);
                var action = TryToInt32(actionValue, out var actionNumber) && Enum.IsDefined((AnalyzerAction)actionNumber)
                    ? (AnalyzerAction)actionNumber
                    : AnalyzerAction.Default;

                // Malformed JSON degrades to an empty list but the row is kept: the
                // analyzer action survives and an empty set just re-triggers analysis.
                var episodeIds = ParseGuidArray(reader.GetValue(3));
                var configHash = ReadOptionalString(reader, hashOrdinal);
                var settled = hasSettled ? ParseGuidArray(reader.GetValue(settledOrdinal)) : [];

                if (existingKeys.Add((seasonId, mode)))
                {
                    states.Add(new DbSeasonState(seasonId, mode, action, settled));
                    imported++;
                }

                // The legacy analyzed-episode list becomes one analysis record per episode,
                // all under the season's hash, so an unchanged configuration does not
                // re-analyze the library after the upgrade.
                foreach (var episodeId in episodeIds)
                {
                    if (!analyzedKeys.Add((episodeId, mode)))
                    {
                        continue;
                    }

                    analyzed.Add(new DbAnalyzedItem(episodeId, mode, configHash));
                    analyzedImported++;

                    if (analyzed.Count >= IntroSkipperDbContext.SaveBatchSize)
                    {
                        await SaveBatchAsync(newDb, newDb.AnalyzedItems, analyzed, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        await SaveBatchAsync(newDb, newDb.SeasonStates, states, cancellationToken).ConfigureAwait(false);
        await SaveBatchAsync(newDb, newDb.AnalyzedItems, analyzed, cancellationToken).ConfigureAwait(false);

        LogSeasonStatesImported(logger, imported, analyzedImported, table);
        return (imported, $"seasons: {table}, {analyzedImported} analysis records");
    }

    // Delegates to the context's shared bounded-batch write, then empties the staging
    // list so the read loop can keep reusing it.
    private static async Task SaveBatchAsync<TEntity>(IntroSkipperDbContext newDb, DbSet<TEntity> set, List<TEntity> pending, CancellationToken cancellationToken)
        where TEntity : class
    {
        await newDb.SaveBatchAsync(set, pending, cancellationToken).ConfigureAwait(false);
        pending.Clear();
    }

    // A -1 ordinal marks a column this legacy shape does not have; NULL and non-text
    // values degrade to empty like every other tolerant read here.
    private static string ReadOptionalString(DbDataReader reader, int ordinal)
        => ordinal >= 0 && reader.GetValue(ordinal) is string value ? value : string.Empty;

    private static Guid[] ParseGuidArray(object? value)
    {
        if (value is not string json)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryReadGuid(object value, out Guid guid)
    {
        // An all-zero id can never name a real Jellyfin item, so a row carrying one
        // is corrupt: skip it rather than import a segment nothing can ever serve.
        switch (value)
        {
            case string text when Guid.TryParse(text, out guid) && guid != Guid.Empty:
                return true;
            case byte[] { Length: 16 } blob:
                guid = new Guid(blob);
                return guid != Guid.Empty;
            default:
                guid = Guid.Empty;
                return false;
        }
    }

    // Repair-era legacy files can hold arbitrarily typed values (SQLite never enforced
    // column affinity), so every numeric read is tolerant: a malformed value skips the
    // row instead of aborting the whole import transaction.
    private static bool TryToDouble(object? value, out double result)
    {
        if (value is not null and not DBNull)
        {
            try
            {
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
            }
        }

        result = 0;
        return false;
    }

    private static bool TryToInt32(object? value, out int result)
    {
        if (TryToInt64(value, out var wide) && wide is >= int.MinValue and <= int.MaxValue)
        {
            result = (int)wide;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryToInt64(object? value, out long result)
    {
        if (value is not null and not DBNull)
        {
            try
            {
                result = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
            }
        }

        result = 0;
        return false;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static HashSet<string> GetColumns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($name)";
        command.Parameters.AddWithValue("$name", tableName);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Imported} legacy segments ({Skipped} skipped as invalid or duplicate)")]
    private static partial void LogSegmentsImported(ILogger logger, int imported, int skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Imported} legacy season states and {Analyzed} analysis records from table {Table}")]
    private static partial void LogSeasonStatesImported(ILogger logger, int imported, int analyzed, string table);
}
