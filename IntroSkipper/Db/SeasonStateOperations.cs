// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Shared operations on <see cref="IntroSkipperDbContext.DbSeasonState"/>, exposed as
/// extension methods on the context. See <see cref="SegmentOperations"/> for the
/// conventions governing this operations layer.
/// </summary>
public static class SeasonStateOperations
{
    /// <summary>
    /// Sets the analyzer actions for a season.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="analyzerActions">Analyzer actions keyed by analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task SetAnalyzerActionAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(analyzerActions);

        var existingEntries = await db.DbSeasonState
            .Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (mode, action) in analyzerActions)
        {
            if (existingEntries.TryGetValue(mode, out var existing))
            {
                db.Entry(existing).Property(s => s.Action).CurrentValue = action;
            }
            else
            {
                db.DbSeasonState.Add(new DbSeasonState(seasonId, mode, action));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the analyzed episode IDs for a season and mode.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeIds">Analyzed episode IDs.</param>
    /// <param name="configHash">Analysis configuration hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task SetEpisodeIdsAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        AnalysisMode mode,
        IEnumerable<Guid> episodeIds,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var seasonState = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonState is null)
        {
            seasonState = new DbSeasonState(seasonId, mode, AnalyzerAction.Default, episodeIds, configHash);
            db.DbSeasonState.Add(seasonState);
        }
        else
        {
            db.Entry(seasonState).Property(s => s.EpisodeIds).CurrentValue = episodeIds;
            db.Entry(seasonState).Property(s => s.ConfigHash).CurrentValue = configHash;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a single episode ID from the season's analyzed-state list for the given mode.
    /// The read and write share the caller's context to keep the window for concurrent
    /// overwrites as small as possible, and the write is skipped entirely when the ID is not
    /// present in the stored list.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeId">Episode ID to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RemoveEpisodeIdAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        AnalysisMode mode,
        Guid episodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var seasonState = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        if (seasonState is null)
        {
            return;
        }

        var currentIds = seasonState.EpisodeIds.ToList();
        if (!currentIds.Remove(episodeId))
        {
            return; // Episode was not in the list — no write needed.
        }

        db.Entry(seasonState).Property(s => s.EpisodeIds).CurrentValue = currentIds;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the analyzed episode IDs for all modes in a season.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Episode IDs keyed by analysis mode.</returns>
    public static async Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetEpisodeIdsAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        return await db.DbSeasonState.Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, s => s.EpisodeIds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the settled-season reanalysis state for all modes in a season.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settled reanalysis state keyed by analysis mode.</returns>
    public static async Task<IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)>> GetSettleReanalysisStatesAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var states = await db.DbSeasonState
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
    /// Records that season analysis modes have been re-analyzed for the given episode set so the
    /// exact completed set is not repeated on subsequent scans or after a plugin restart. Call only
    /// after the reset has committed.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="modes">Analysis modes that were re-analyzed.</param>
    /// <param name="episodeIds">Episode IDs that were re-analyzed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RecordSettleReanalysisAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(episodeIds);

        if (modes.Count == 0)
        {
            return;
        }

        var settledEpisodeIds = DbSeasonState.SerializeEpisodeIds(episodeIds);

        foreach (var mode in modes)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "DbSeasonState" ("SeasonId", "Type", "Action", "EpisodeIds", "ConfigHash", "SettledReanalysisEpisodeIds")
                VALUES ({seasonId}, {(int)mode}, {(int)AnalyzerAction.Default}, {"[]"}, {string.Empty}, {settledEpisodeIds})
                ON CONFLICT("SeasonId", "Type") DO UPDATE SET
                    "SettledReanalysisEpisodeIds" = excluded."SettledReanalysisEpisodeIds"
                """,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears stored automatic analysis state for a season so it is re-analyzed from scratch on the
    /// current pass. Automatic segments for the supplied modes are deleted and the season's analyzed
    /// episode lists are cleared; user-provided segments and the fingerprint cache are preserved.
    /// The deletes and the episode-list clear run in a single transaction so a cancelled reset cannot
    /// leave segments deleted while their episodes are still recorded as analyzed.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID whose analyzed-state lists should be cleared.</param>
    /// <param name="episodeIds">Episode IDs whose automatic segments should be removed.</param>
    /// <param name="modes">Analysis modes to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task ResetSeasonForReanalysisAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        IEnumerable<Guid> episodeIds,
        IReadOnlyCollection<AnalysisMode> modes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(episodeIds);
        ArgumentNullException.ThrowIfNull(modes);

        var ids = episodeIds.Distinct().ToArray();
        var modeArray = modes.ToArray();
        if (ids.Length == 0 || modeArray.Length == 0)
        {
            return;
        }

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Single set-based delete; the ID collection travels as one JSON parameter
            // (EF.Parameter → json_each on SQLite), immune to the host-parameter limit.
            await db.DbSegment
                .Where(s => EF.Parameter(ids).Contains(s.ItemId) && modeArray.Contains(s.Type) && !s.IsUserProvided)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            // Clear the analyzed-episode lists so VerifyQueueAsync treats every episode as NotAnalyzed.
            // Committing this together with the deletes guarantees the episodes are re-analyzed (either
            // on this pass or a later one) instead of being stranded as NoSegments.
            var seasonStates = await db.DbSeasonState
                .Where(s => s.SeasonId == seasonId && modeArray.Contains(s.Type))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var state in seasonStates)
            {
                db.Entry(state).Property(s => s.EpisodeIds).CurrentValue = [];
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads a combined snapshot of season state and segments for a season in two round-trips.
    /// The episode ID collection travels as a single JSON parameter (<see cref="EF.Parameter{T}(T)"/>
    /// → <c>json_each</c> on SQLite), so it is not subject to the SQLite host-parameter limit.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="episodeIds">Episode IDs in the season.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The season queue snapshot.</returns>
    internal static async Task<SeasonQueueSnapshot> GetSeasonQueueSnapshotAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(episodeIds);

        var episodeIdArray = (Guid[])[.. episodeIds.Distinct()];

        var seasonStates = await db.DbSeasonState
            .AsNoTracking()
            .Where(s => s.SeasonId == seasonId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var segments = await db.DbSegment
            .AsNoTracking()
            .Where(s => EF.Parameter(episodeIdArray).Contains(s.ItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SeasonQueueSnapshot(
            seasonStates.ToDictionary(s => s.Type, s => (IReadOnlySet<Guid>)s.EpisodeIds.ToHashSet()),
            seasonStates.ToDictionary(s => s.Type, s => s.ConfigHash),
            seasonStates.ToDictionary(s => s.Type, s => s.Action),
            segments
                .GroupBy(s => s.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => SegmentOperations.ToTimestampDictionary([.. group])),
            segments
                .Where(s => s.IsUserProvided)
                .GroupBy(s => s.Type)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlySet<Guid>)group.Select(s => s.ItemId).ToHashSet()));
    }

    /// <summary>
    /// Returns the analyzer actions for all modes in a season, filling in defaults for missing modes.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analyzer actions keyed by analysis mode.</returns>
    public static async Task<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAllAnalyzerActionsAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var states = await db.DbSeasonState
            .Where(s => s.SeasonId == seasonId)
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
    /// Returns the analyzer action for a season and mode, or the default action when unset.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The analyzer action.</returns>
    public static async Task<AnalyzerAction> GetAnalyzerActionAsync(
        this IntroSkipperDbContext db,
        Guid seasonId,
        AnalysisMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var state = await db.DbSeasonState
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);
        return state?.Action ?? AnalyzerAction.Default;
    }

    /// <summary>
    /// Deletes season state for seasons that are no longer present in the library.
    /// The ID collection travels as a single JSON parameter (<see cref="EF.Parameter{T}(T)"/>
    /// → <c>json_each</c> on SQLite), so it is not subject to the SQLite host-parameter limit.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="validSeasonIds">Season IDs that still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task CleanSeasonStateAsync(
        this IntroSkipperDbContext db,
        IEnumerable<Guid> validSeasonIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(validSeasonIds);

        var seasonIds = validSeasonIds.Distinct().ToArray();

        await db.DbSeasonState
            .Where(s => !EF.Parameter(seasonIds).Contains(s.SeasonId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
