// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Season-state (<see cref="DbSeasonState"/>) operations of <see cref="IntroSkipperDatabase"/>,
/// plus the queue-verification snapshot that joins season state, analysis records and segments.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    private const string PreviewSettingMigratedKey = "preview-from-credits-end-migrated";

    /// <summary>Returns the optional analysis-window overrides for a season.</summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The saved overrides. <see cref="AnalysisOverrides.PreviewFromCreditsEnd"/> is
    /// <see langword="null"/> only when the season has no stored choice and the
    /// preview-setting migration has not completed, so the removed plugin-wide switch still
    /// decides it. Callers resolve that with
    /// <see cref="Configuration.PluginConfiguration.LegacyPreviewFromCreditsEnd"/>. After the
    /// migration an unset choice reads as disabled.
    /// </returns>
    public async Task<AnalysisOverrides> GetAnalysisOverridesAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.Database
            .SqlQuery<AnalysisOverrides>(
                $"""
                SELECT
                    "o"."AnalysisPercent",
                    "o"."AnalysisLengthLimit",
                    COALESCE("o"."PreviewFromCreditsEnd", CASE WHEN EXISTS (
                        SELECT 1 FROM "PluginMetadata" WHERE "Key" = {PreviewSettingMigratedKey} AND "Value" = 'true'
                    ) THEN 0 END) AS "PreviewFromCreditsEnd",
                    COALESCE("o"."PreviewFromCreditsEndRecommended", 0) AS "PreviewFromCreditsEndRecommended",
                    "o"."SubtitleRecapDetection",
                    "o"."SubtitlePreviewDetection"
                FROM (SELECT 1) AS "one"
                LEFT JOIN "SeasonAnalysisOverrides" AS "o" ON "o"."SeasonId" = {seasonId}
                """)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Stores analysis-window overrides for a season, or clears them when all are unset.</summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="analysisPercent">Percentage override, or null to inherit.</param>
    /// <param name="analysisLengthLimit">Runtime limit override in minutes, or null to inherit.</param>
    /// <param name="previewFromCreditsEnd">Whether to derive a preview after credits, or null to preserve the current choice.</param>
    /// <param name="subtitleRecapDetection">Whether to enable subtitle recap detection, or null to inherit.</param>
    /// <param name="subtitlePreviewDetection">Whether to enable subtitle preview detection, or null to inherit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetAnalysisOverridesAsync(
        Guid seasonId,
        int? analysisPercent,
        int? analysisLengthLimit,
        bool? previewFromCreditsEnd,
        bool? subtitleRecapDetection,
        bool? subtitlePreviewDetection,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        if (analysisPercent is null
            && analysisLengthLimit is null
            && previewFromCreditsEnd is null
            && subtitleRecapDetection is null
            && subtitlePreviewDetection is null)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE "SeasonAnalysisOverrides"
                SET "AnalysisPercent" = NULL,
                    "AnalysisLengthLimit" = NULL,
                    "SubtitleRecapDetection" = NULL,
                    "SubtitlePreviewDetection" = NULL
                WHERE "SeasonId" = {seasonId}
                """,
                cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "SeasonAnalysisOverrides" ("SeasonId", "AnalysisPercent", "AnalysisLengthLimit", "PreviewFromCreditsEnd", "SubtitleRecapDetection", "SubtitlePreviewDetection")
            VALUES ({seasonId}, {analysisPercent}, {analysisLengthLimit}, {previewFromCreditsEnd}, {subtitleRecapDetection}, {subtitlePreviewDetection})
            ON CONFLICT("SeasonId") DO UPDATE SET
                "AnalysisPercent" = excluded."AnalysisPercent",
                "AnalysisLengthLimit" = excluded."AnalysisLengthLimit",
                "PreviewFromCreditsEnd" = COALESCE(excluded."PreviewFromCreditsEnd", "SeasonAnalysisOverrides"."PreviewFromCreditsEnd"),
                "SubtitleRecapDetection" = excluded."SubtitleRecapDetection",
                "SubtitlePreviewDetection" = excluded."SubtitlePreviewDetection"
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the analysis-derived recommendation without changing the season's choice.</summary>
    /// <param name="seasonId">Season key.</param>
    /// <param name="recommended">Whether recurring short post-credits tails were found.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after the recommendation is stored.</returns>
    public async Task SetPreviewFromCreditsEndRecommendationAsync(Guid seasonId, bool recommended, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "SeasonAnalysisOverrides" ("SeasonId", "PreviewFromCreditsEndRecommended")
            VALUES ({seasonId}, {recommended})
            ON CONFLICT("SeasonId") DO UPDATE SET
                "PreviewFromCreditsEndRecommended" = excluded."PreviewFromCreditsEndRecommended"
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets whether the legacy global post-credits preview setting has been migrated.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> once the migration's choices and marker are stored.</returns>
    public async Task<bool> IsPreviewSettingMigrationCompleteAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.PluginMetadata
            .AsNoTracking()
            .AnyAsync(row => row.Key == PreviewSettingMigratedKey && row.Value == "true", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Completes the preview-setting migration. One transaction enables the post-credits
    /// preview for each given season that has no choice and stores the completion marker.
    /// Every other season reads disabled once the marker exists. The method leaves stored
    /// choices and every other override untouched.
    /// </summary>
    /// <param name="enabledSeasonIds">The seasons the removed plugin-wide switch enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after the choices and the marker are committed.</returns>
    public Task CompletePreviewSettingMigrationAsync(IEnumerable<Guid> enabledSeasonIds, CancellationToken cancellationToken = default)
    {
        var choices = MultiRowSql.Statements(
            enabledSeasonIds.Distinct(),
            id => $"({id}, 1)",
            values => $"""
                INSERT INTO "SeasonAnalysisOverrides" ("SeasonId", "PreviewFromCreditsEnd")
                VALUES {values}
                ON CONFLICT("SeasonId") DO UPDATE SET "PreviewFromCreditsEnd" = excluded."PreviewFromCreditsEnd"
                WHERE "SeasonAnalysisOverrides"."PreviewFromCreditsEnd" IS NULL
                """);
        FormattableString marker = $"""
            INSERT INTO "PluginMetadata" ("Key", "Value")
            VALUES ({PreviewSettingMigratedKey}, 'true')
            ON CONFLICT("Key") DO UPDATE SET "Value" = excluded."Value"
            """;
        return ExecuteInTransactionAsync([.. choices, marker], cancellationToken);
    }

    /// <summary>
    /// Sets the analyzer actions for a season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="analyzerActions">Analyzer actions keyed by analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task SetAnalyzerActionAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // One transaction so a cancelled request cannot leave the season with a
        // partially updated action set.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            foreach (var (mode, action) in analyzerActions)
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO "SeasonStates" ("SeasonId", "Type", "Action", "SettledReanalysisEpisodeIds")
                    VALUES ({seasonId}, {(int)mode}, {(int)action}, '[]')
                    ON CONFLICT("SeasonId", "Type") DO UPDATE SET "Action" = excluded."Action"
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the analyzer action and settled-season reanalysis state for every mode
    /// of a season that has a stored row; modes without a row are absent and mean
    /// <see cref="AnalyzerAction.Default"/>.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settled reanalysis state keyed by analysis mode.</returns>
    public async Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var states = await db.SeasonStates
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .Select(s => new
            {
                s.Type,
                s.Action,
                s.SettledReanalysisEpisodeIds
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return states.ToDictionary(
            s => s.Type,
            s => (s.Action, (IReadOnlySet<Guid>)s.SettledReanalysisEpisodeIds.ToHashSet()));
    }

    /// <summary>
    /// Records that season analysis modes have been re-analyzed for the given episode set.
    /// Call only for modes whose reanalysis completed.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="modes">Analysis modes that were re-analyzed.</param>
    /// <param name="episodeIds">Episode IDs that were re-analyzed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task RecordSettleReanalysisAsync(
        Guid seasonId,
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken cancellationToken = default)
    {
        if (modes.Count == 0)
        {
            return;
        }

        // Same JSON shape EF writes for the primitive collection, so the raw upsert and
        // tracked reads agree.
        var settledEpisodeIds = JsonSerializer.Serialize(episodeIds, (JsonSerializerOptions?)null);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        foreach (var mode in modes)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "SeasonStates" ("SeasonId", "Type", "Action", "SettledReanalysisEpisodeIds")
                VALUES ({seasonId}, {(int)mode}, {(int)AnalyzerAction.Default}, {settledEpisodeIds})
                ON CONFLICT("SeasonId", "Type") DO UPDATE SET
                    "SettledReanalysisEpisodeIds" = excluded."SettledReanalysisEpisodeIds"
                """,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the analyzer action for every analysis mode of a season, filling in
    /// <see cref="AnalyzerAction.Default"/> for modes without a stored row.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analyzer actions keyed by analysis mode.</returns>
    public async Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var states = await db.SeasonStates
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .Select(s => new { s.Type, s.Action })
            .ToDictionaryAsync(s => s.Type, s => s.Action, cancellationToken)
            .ConfigureAwait(false);

        // Fill in defaults for any missing modes
        var result = new Dictionary<AnalysisMode, AnalyzerAction>();
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            result[mode] = states.TryGetValue(mode, out var action) ? action : AnalyzerAction.Default;
        }

        return result;
    }

    /// <summary>
    /// Returns a snapshot of the season's analyzer actions, the episodes' analysis records
    /// and the modes their active segments cover, used by queue verification to avoid
    /// per-episode database lookups. The episode ID set is bound as one JSON parameter, so
    /// the episode count is unbounded.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="episodeIds">Episode IDs in the season.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season queue snapshot.</returns>
    public async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(Guid seasonId, IReadOnlyCollection<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var episodeIdArray = (Guid[])[.. episodeIds.Distinct()];

        var analyzerActions = await db.SeasonStates
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .Select(s => new { s.Type, s.Action })
            .ToDictionaryAsync(s => s.Type, s => s.Action, cancellationToken)
            .ConfigureAwait(false);

        var analyzed = await db.AnalyzedItems
            .AsNoTracking()
            .Where(a => EF.Parameter(episodeIdArray).Contains(a.ItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Project only the consumed columns: queue verification tests mode presence and
        // user provenance, nothing else, so the tick, Id, ConfigHash and timestamp columns
        // are all skipped. That avoids a per-row Guid parse plus two DateTime TEXT parses
        // on a query that runs once per season per scan, and keeps seconds-typed segment
        // payloads off this internal path (ticks stay internal).
        var segments = await db.Segments
            .AsNoTracking()
            .Where(s => EF.Parameter(episodeIdArray).Contains(s.ItemId) && s.State == SegmentState.Active)
            .Select(s => new { s.ItemId, s.Type, s.Source })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SeasonQueueSnapshot(
            analyzed.ToDictionary(a => (a.ItemId, a.Type), a => new AnalysisRecord(a.ConfigHash, a.FileVersion, a.KeepHash)),
            analyzerActions,
            segments
                .GroupBy(s => s.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<AnalysisMode>)group.Select(s => s.Type).ToHashSet()),
            segments
                .Where(s => s.Source == SegmentSource.User)
                .GroupBy(s => s.Type)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<Guid>)group.Select(s => s.ItemId).ToHashSet()));
    }
}
