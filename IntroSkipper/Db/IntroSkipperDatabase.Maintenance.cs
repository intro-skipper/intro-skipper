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
    public async Task CleanTimestampsAsync(IEnumerable<Guid> enabledEpisodeIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enabledEpisodeIds);

        var enabledIds = enabledEpisodeIds.ToHashSet();

        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var segmentEpisodeIds = await db.DbSegment
            .AsNoTracking()
            .Select(s => s.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var staleEpisodeIds = segmentEpisodeIds
            .Where(id => !enabledIds.Contains(id))
            .ToArray();

        foreach (var staleEpisodeIdBatch in staleEpisodeIds.Chunk(SqliteParameterBatchSize))
        {
            await db.DbSegment
                .Where(s => staleEpisodeIdBatch.Contains(s.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task CleanStaleAutomaticSegmentsAsync(
        IEnumerable<Guid> itemIds,
        AnalysisMode mode,
        string configHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        foreach (var batch in ids.Chunk(SqliteParameterBatchSize))
        {
            await db.DbSegment
                .Where(s => batch.Contains(s.ItemId)
                    && s.Type == mode
                    && !s.IsUserProvided
                    && s.ConfigHash != configHash)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
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

        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var batch in ids.Chunk(SqliteParameterBatchSize))
            {
                await db.DbSegment
                    .Where(s => batch.Contains(s.ItemId) && modeArray.Contains(s.Type) && !s.IsUserProvided)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

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

    /// <inheritdoc/>
    public async Task CleanSeasonStateAsync(IEnumerable<Guid> seasonIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seasonIds);

        var retainedIds = seasonIds.ToHashSet();

        await EnsureInitializedAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // Compute the stale key set client-side and delete in chunks. Translating the
        // NOT-IN directly would bind one SQLite parameter per retained season and
        // overflow the 32,766-variable limit for very large libraries.
        var storedSeasonIds = await db.DbSeasonState
            .AsNoTracking()
            .Select(s => s.SeasonId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var staleSeasonIds = storedSeasonIds
            .Where(id => !retainedIds.Contains(id))
            .ToArray();

        foreach (var staleSeasonIdBatch in staleSeasonIds.Chunk(SqliteParameterBatchSize))
        {
            await db.DbSeasonState
                .Where(s => staleSeasonIdBatch.Contains(s.SeasonId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
