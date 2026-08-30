// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Bulk maintenance operations of <see cref="IntroSkipperDatabase"/> spanning
/// segments and season state.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Guid>> GetStaleTimestampEpisodeIdsAsync(
        IEnumerable<Guid> enabledEpisodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enabledEpisodeIds);

        var enabledIds = enabledEpisodeIds.Distinct().ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // EF.Parameter forces the single-JSON-parameter json_each translation on SQLite,
        // so the retained set is one bound parameter regardless of its size — no
        // 32,766-variable limit and no chunking (verified by a 33,000-ID test).
        return await db.DbSegment
            .Where(s => !EF.Parameter(enabledIds).Contains(s.ItemId))
            .Select(s => s.ItemId)
            .Distinct()
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> CleanStaleAutomaticSegmentsAsync(
        IEnumerable<Guid> itemIds,
        AnalysisMode mode,
        string configHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await db.DbSegment
            .Where(s => EF.Parameter(ids).Contains(s.ItemId)
                && s.Type == mode
                && !s.IsUserProvided
                && (s.ConfigHash != configHash || s.End <= 0.0 || s.End <= s.Start))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ClearSeasonAnalysisAsync(
        Guid seasonId,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Distinct().ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (ids.Length > 0)
            {
                await db.DbSegment
                    .Where(s => EF.Parameter(ids).Contains(s.ItemId))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await db.DbSeasonState
                .Where(s => s.SeasonId == seasonId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(s => s.EpisodeIds, Array.Empty<Guid>()),
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<int> RemoveItemsFromAnalysisAsync(
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> itemIdsBySeason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIdsBySeason);

        if (itemIdsBySeason.Count == 0)
        {
            return 0;
        }

        var itemIds = itemIdsBySeason.Values
            .SelectMany(static ids => ids)
            .Distinct()
            .ToArray();
        var seasonIds = itemIdsBySeason.Keys.ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var removedSegments = itemIds.Length == 0
                ? 0
                : await db.DbSegment
                    .Where(s => EF.Parameter(itemIds).Contains(s.ItemId))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

            var seasonStates = await db.DbSeasonState
                .Where(s => EF.Parameter(seasonIds).Contains(s.SeasonId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var state in seasonStates)
            {
                var currentIds = state.EpisodeIds.ToList();
                if (currentIds.RemoveAll(itemIdsBySeason[state.SeasonId].Contains) > 0)
                {
                    db.Entry(state).Property(s => s.EpisodeIds).CurrentValue = currentIds;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return removedSegments;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Segment deletion and removal from both analyzed-episode sets are committed together so a
    /// failed reset cannot leave stale state marked as analyzed.
    /// </remarks>
    public async Task ResetItemForReanalysisAsync(
        Guid itemId,
        IReadOnlyCollection<AnalysisMode> modes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modes);

        var modeArray = modes.Distinct().ToArray();
        if (itemId == Guid.Empty || modeArray.Length == 0)
        {
            return;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await db.DbSegment
                .Where(s => s.ItemId == itemId && modeArray.Contains(s.Type) && !s.IsUserProvided)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            // No single season id reliably locates the rows referencing an item (see #936), so
            // match on the episode-id lists themselves. NOCASE is required because EF writes
            // uppercase GUID JSON while SerializeEpisodeIds and migrated rows are lowercase.
            var itemIdText = itemId.ToString();
            var seasonStates = await db.DbSeasonState
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM "DbSeasonState"
                    WHERE EXISTS (SELECT 1 FROM json_each("EpisodeIds") WHERE "value" = {itemIdText} COLLATE NOCASE)
                       OR EXISTS (SELECT 1 FROM json_each("SettledReanalysisEpisodeIds") WHERE "value" = {itemIdText} COLLATE NOCASE)
                    """)
                .Where(s => modeArray.Contains(s.Type))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var state in seasonStates)
            {
                var episodeIds = state.EpisodeIds.ToList();
                var settledReanalysisEpisodeIds = state.SettledReanalysisEpisodeIds.ToList();

                if (episodeIds.Remove(itemId))
                {
                    db.Entry(state).Property(s => s.EpisodeIds).CurrentValue = episodeIds;
                }

                if (settledReanalysisEpisodeIds.Remove(itemId))
                {
                    db.Entry(state).Property(s => s.SettledReanalysisEpisodeIds).CurrentValue = settledReanalysisEpisodeIds;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The segment deletes and the episode-list clear run in a single transaction so a
    /// cancelled reset cannot leave segments deleted while their episodes are still
    /// recorded as analyzed.
    /// </remarks>
    public async Task ResetSeasonForReanalysisAsync(
        Guid seasonId,
        IEnumerable<Guid> episodeIds,
        IReadOnlyCollection<AnalysisMode> modes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episodeIds);
        ArgumentNullException.ThrowIfNull(modes);

        var ids = episodeIds.Distinct().ToArray();
        var modeArray = modes.ToArray();
        if (ids.Length == 0 || modeArray.Length == 0)
        {
            return;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await db.DbSegment
                .Where(s => EF.Parameter(ids).Contains(s.ItemId) && modeArray.Contains(s.Type) && !s.IsUserProvided)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            // Clear the analyzed-episode lists so VerifyQueueAsync treats every episode as NotAnalyzed.
            // Committing this together with the deletes guarantees the episodes are re-analyzed (either
            // on this pass or a later one) instead of being stranded as NoSegments.
            await db.DbSeasonState
                .Where(s => s.SeasonId == seasonId && modeArray.Contains(s.Type))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(s => s.EpisodeIds, Array.Empty<Guid>()),
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task CleanSeasonStateAsync(IEnumerable<Guid> seasonIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seasonIds);

        var retainedIds = seasonIds.Distinct().ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // Single NOT-IN delete; EF.Parameter binds the retained set as one JSON
        // parameter, so this is safe for arbitrarily large libraries.
        await db.DbSeasonState
            .Where(s => !EF.Parameter(retainedIds).Contains(s.SeasonId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
