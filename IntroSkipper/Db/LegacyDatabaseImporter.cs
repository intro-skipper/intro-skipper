// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

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
    private const int SaveBatchSize = 1000;

    /// <summary>
    /// Imports segments and season states from the legacy database into <paramref name="newDb"/>.
    /// The caller owns the surrounding transaction and the <see cref="DbImportRecord"/> marker;
    /// this method only reads the legacy file and stages/saves rows on the new context.
    /// </summary>
    /// <param name="newDb">Context of the current database.</param>
    /// <param name="legacyDbPath">Path of the legacy database file (must exist).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The import counts.</returns>
    internal static async Task<LegacyImportResult> ImportAsync(
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

        return new LegacyImportResult(segmentsImported, segmentsSkipped, statesImported, $"{segmentNotes}; {stateNotes}");
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

        // Quadruples already present in the new database (crash-retry safety: a previous
        // import attempt may have saved rows before its marker committed).
        var seen = new HashSet<(Guid ItemId, AnalysisMode Type, long StartTicks, long EndTicks)>();
        var existing = await newDb.Segments
            .AsNoTracking()
            .Select(s => new { s.ItemId, s.Type, s.StartTicks, s.EndTicks })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in existing)
        {
            seen.Add((row.ItemId, row.Type, row.StartTicks, row.EndTicks));
        }

        using var command = legacy.CreateCommand();

        // User rows first so they win the exact-duplicate dedupe below.
#pragma warning disable CA2100 // Interpolation only splices compile-time literals selected by column presence.
        command.CommandText =
            $"""
            SELECT "ItemId", "Start", "End", "Type"
                {(hasUser ? ", \"IsUserProvided\"" : string.Empty)}
                {(hasHash ? ", \"ConfigHash\"" : string.Empty)}
            FROM "DbSegment"
            {(hasUser ? "ORDER BY \"IsUserProvided\" DESC" : string.Empty)}
            """;
#pragma warning restore CA2100

        var imported = 0;
        var skipped = 0;
        var pending = new List<DbSegment>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var startValue = reader.GetValue(1);
                var endValue = reader.GetValue(2);
                var typeValue = reader.GetValue(3);
                if (!TryReadGuid(reader.GetValue(0), out var itemId)
                    || startValue is DBNull
                    || endValue is DBNull
                    || typeValue is DBNull)
                {
                    skipped++;
                    continue;
                }

                var startSeconds = Convert.ToDouble(startValue, CultureInfo.InvariantCulture);
                var endSeconds = Convert.ToDouble(endValue, CultureInfo.InvariantCulture);
                var type = Convert.ToInt32(typeValue, CultureInfo.InvariantCulture);

                if (!Enum.IsDefined((AnalysisMode)type)
                    || !TickConversions.TryFromSecondsRange(startSeconds, endSeconds, out var startTicks, out var endTicks))
                {
                    skipped++;
                    continue;
                }

                if (!seen.Add((itemId, (AnalysisMode)type, startTicks, endTicks)))
                {
                    skipped++;
                    continue;
                }

                var userValue = hasUser ? reader.GetValue(4) : null;
                var source = userValue is not null and not DBNull
                             && Convert.ToInt64(userValue, CultureInfo.InvariantCulture) != 0
                    ? SegmentSource.User
                    : SegmentSource.Unknown;
                var hashOrdinal = hasUser ? 5 : 4;
                var configHash = hasHash && reader.GetValue(hashOrdinal) is string hash ? hash : string.Empty;

                pending.Add(new DbSegment(itemId, (AnalysisMode)type, startTicks, endTicks, source, configHash));
                imported++;

                if (pending.Count >= SaveBatchSize)
                {
                    await SaveBatchAsync(newDb, pending, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await SaveBatchAsync(newDb, pending, cancellationToken).ConfigureAwait(false);

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
        var states = new List<DbSeasonState>();

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var typeValue = reader.GetValue(1);
                if (!TryReadGuid(reader.GetValue(0), out var seasonId) || typeValue is DBNull)
                {
                    continue;
                }

                var type = Convert.ToInt32(typeValue, CultureInfo.InvariantCulture);
                if (!Enum.IsDefined((AnalysisMode)type) || !existingKeys.Add((seasonId, (AnalysisMode)type)))
                {
                    continue;
                }

                var actionValue = reader.GetValue(2);
                var action = actionValue is not DBNull
                             && Convert.ToInt32(actionValue, CultureInfo.InvariantCulture) is var actionNumber
                             && Enum.IsDefined((AnalyzerAction)actionNumber)
                    ? (AnalyzerAction)actionNumber
                    : AnalyzerAction.Default;

                // Malformed JSON degrades to an empty list but the row is kept: the
                // analyzer action survives and an empty set just re-triggers analysis.
                var episodeIds = ParseGuidArray(reader.GetValue(3));
                var hashOrdinal = 4;
                var configHash = hasHash && reader.GetValue(hashOrdinal) is string hash ? hash : string.Empty;
                var settledOrdinal = hasHash ? 5 : 4;
                var settled = hasSettled ? ParseGuidArray(reader.GetValue(settledOrdinal)) : [];

                states.Add(new DbSeasonState(seasonId, (AnalysisMode)type, action, episodeIds, configHash, settled));
                imported++;
            }
        }

        if (states.Count > 0)
        {
            newDb.SeasonStates.AddRange(states);
            await newDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        LogSeasonStatesImported(logger, imported, table);
        return (imported, $"seasons: {table}");
    }

    private static async Task SaveBatchAsync(IntroSkipperDbContext newDb, List<DbSegment> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        newDb.Segments.AddRange(pending);
        await newDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        newDb.ChangeTracker.Clear();
        pending.Clear();
    }

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
        switch (value)
        {
            case string text when Guid.TryParse(text, out guid):
                return true;
            case byte[] { Length: 16 } blob:
                guid = new Guid(blob);
                return true;
            default:
                guid = Guid.Empty;
                return false;
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported {Imported} legacy season states from table {Table}")]
    private static partial void LogSeasonStatesImported(ILogger logger, int imported, string table);
}
