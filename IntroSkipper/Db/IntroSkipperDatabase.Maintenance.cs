// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Bulk maintenance operations of <see cref="IntroSkipperDatabase"/> spanning
/// segments, analysis records and season state.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <summary>
    /// Gets the IDs of items with segments (including tombstones) that are no longer
    /// part of any enabled library.
    /// </summary>
    /// <param name="enabledEpisodeIds">Episode IDs that are still part of enabled libraries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stale episode IDs.</returns>
    public async Task<IReadOnlyCollection<Guid>> GetStaleTimestampEpisodeIdsAsync(
        IEnumerable<Guid> enabledEpisodeIds,
        CancellationToken cancellationToken = default)
    {
        var enabledIds = enabledEpisodeIds.Distinct().ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // EF.Parameter forces the single-JSON-parameter json_each translation on SQLite,
        // so the retained set is one bound parameter regardless of its size — no
        // 32,766-variable limit and no chunking (verified by a 33,000-ID test).
        return await db.Segments
            .Where(s => !EF.Parameter(enabledIds).Contains(s.ItemId))
            .Select(s => s.ItemId)
            .Distinct()
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the pass's stale automatic segments for the supplied items: active rows
    /// whose <see cref="DbSegment.ConfigHash"/> is non-empty and differs from
    /// <paramref name="configHash"/>. Credits-derived rows belong to the credits pass.
    /// User segments, tombstones, rows with an empty hash and the automatic rows of a
    /// type the item holds an active user row for are kept. The affected items'
    /// projections are journaled with the delete.
    /// </summary>
    /// <param name="itemIds">Item IDs to inspect.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="configHash">Current configuration hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows removed.</returns>
    public async Task<int> CleanStaleAutomaticSegmentsAsync(
        IEnumerable<Guid> itemIds,
        AnalysisMode mode,
        string configHash,
        CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // Credits-derived previews carry the credits hash (the credits pass produces
        // them), so the credits pass judges their staleness and the preview pass leaves
        // them alone; every other automatic row belongs to its own mode's pass.
        // A row with an empty hash makes no staleness claim and is kept: restored
        // tombstones drop their hash on purpose, and legacy imports without a recorded
        // hash are replaced by re-analysis rather than deleted ahead of it.
        // The NOT EXISTS guard keeps the facade from ever deleting automatic rows of a
        // type the user has an active row for — the analyzers skip such items, so
        // nothing would regenerate the rows (same guard as ResetItemsForReanalysisAsync;
        // callers additionally pre-filter user-provided items as an optimization).
        var staleRows = db.Segments
            .Where(s => EF.Parameter(ids).Contains(s.ItemId)
                && s.Source != SegmentSource.User
                && s.State == SegmentState.Active
                && s.ConfigHash != string.Empty
                && s.ConfigHash != configHash
                && ((s.Source != SegmentSource.CreditsDerived && s.Type == mode)
                    || (s.Source == SegmentSource.CreditsDerived && mode == AnalysisMode.Credits))
                && !db.Segments.Any(u => u.ItemId == s.ItemId
                    && u.Type == s.Type
                    && u.Source == SegmentSource.User
                    && u.State == SegmentState.Active));

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // Journaled with the delete; see docs/segments.md.
            var (removed, _) = await DeleteSegmentsAndJournalAsync(db, staleRows, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return removed;
        }
    }

    /// <summary>
    /// Erases the supplied items: every segment (tombstones included) and every analysis
    /// record, in one transaction, with every item's projection journaled. Season state
    /// and disable flags are untouched. The ID set is bound as one JSON parameter, so
    /// the item count is unbounded.
    /// </summary>
    /// <param name="itemIds">Item IDs to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted segment rows.</returns>
    public async Task<int> EraseItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // Journal every addressed item, rows or not: an erase is the user saying
            // "this item must serve nothing", and an item with zero plugin rows can
            // still hold ghost Jellyfin rows (a past projection whose plugin rows were
            // since lost) that only a projection heals. Erases are explicit user
            // actions over bounded id sets, so the extra markers cost one no-op sync each.
            var removedSegments = await db.Segments
                .Where(s => EF.Parameter(ids).Contains(s.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await db.AnalyzedItems
                .Where(a => EF.Parameter(ids).Contains(a.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await EnqueueProjectionsAsync(db, ids, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return removedSegments;
        }
    }

    /// <summary>
    /// Clears the items' automatic segments and analysis records for the modes in one
    /// transaction so the current pass re-analyzes them from scratch. User segments,
    /// tombstones and the automatic rows of a type the item holds an active user row
    /// for are kept. Items whose rows were deleted journal their projections with the
    /// reset.
    /// </summary>
    /// <param name="itemIds">Item IDs to reset.</param>
    /// <param name="modes">Analysis modes to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ResetItemsForReanalysisAsync(
        IEnumerable<Guid> itemIds,
        IReadOnlyCollection<AnalysisMode> modes,
        CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
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
            var doomedRows = db.Segments
                .Where(s => EF.Parameter(ids).Contains(s.ItemId)
                    && modeArray.Contains(s.Type)
                    && s.Source != SegmentSource.User
                    && s.State == SegmentState.Active
                    && !db.Segments.Any(u => u.ItemId == s.ItemId
                        && u.Type == s.Type
                        && u.Source == SegmentSource.User
                        && u.State == SegmentState.Active));

            // Journaled with the delete; see docs/segments.md.
            await DeleteSegmentsAndJournalAsync(db, doomedRows, cancellationToken).ConfigureAwait(false);

            // Without their records the items are NotAnalyzed on this pass (or a later
            // one) instead of being stranded as NoSegments.
            await db.AnalyzedItems
                .Where(a => EF.Parameter(ids).Contains(a.ItemId) && modeArray.Contains(a.Type))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the season-state keys that are not part of <paramref name="retainedSeasonIds"/>,
    /// so cleanup can decide per key whether the season is gone or merely missing from an
    /// enumeration that skipped its library.
    /// </summary>
    /// <param name="retainedSeasonIds">Season IDs known to still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stale season-state keys.</returns>
    public async Task<IReadOnlyCollection<Guid>> GetStaleSeasonIdsAsync(IEnumerable<Guid> retainedSeasonIds, CancellationToken cancellationToken = default)
    {
        var retainedIds = retainedSeasonIds.Distinct().ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await db.SeasonStates
            .Where(s => !EF.Parameter(retainedIds).Contains(s.SeasonId))
            .Select(s => s.SeasonId)
            .Union(db.SeasonAnalysisOverrides
                .Where(s => !EF.Parameter(retainedIds).Contains(s.SeasonId))
                .Select(s => s.SeasonId))
            .Distinct()
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the item IDs holding per-item state (disable flags or analysis records) that
    /// are not part of <paramref name="retainedItemIds"/>, so cleanup can decide per item
    /// whether it is gone or merely missing from an enumeration that skipped its library.
    /// </summary>
    /// <param name="retainedItemIds">Item IDs known to still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stale per-item-state item IDs.</returns>
    public async Task<IReadOnlyCollection<Guid>> GetStaleItemStateIdsAsync(IReadOnlyCollection<Guid> retainedItemIds, CancellationToken cancellationToken = default)
    {
        var retainedIds = retainedItemIds.Distinct().ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // One UNION statement; the set operator already deduplicates.
        return await db.DisabledItems
            .Where(e => !EF.Parameter(retainedIds).Contains(e.ItemId))
            .Select(e => e.ItemId)
            .Union(db.AnalyzedItems
                .Where(a => !EF.Parameter(retainedIds).Contains(a.ItemId))
                .Select(a => a.ItemId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes season-state rows whose seasons no longer exist.
    /// </summary>
    /// <param name="seasonIds">Season IDs that still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task CleanSeasonStateAsync(IEnumerable<Guid> seasonIds, CancellationToken cancellationToken = default)
    {
        var retainedIds = seasonIds.Distinct().ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // Single NOT-IN delete; EF.Parameter binds the retained set as one JSON
        // parameter, so this is safe for arbitrarily large libraries.
        await db.SeasonStates
            .Where(s => !EF.Parameter(retainedIds).Contains(s.SeasonId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.SeasonAnalysisOverrides
            .Where(s => !EF.Parameter(retainedIds).Contains(s.SeasonId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes per-item state (disable flags and analysis records) of items that no
    /// longer exist in enabled libraries.
    /// </summary>
    /// <param name="retainedItemIds">Item IDs that still exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task CleanItemStateAsync(IReadOnlyCollection<Guid> retainedItemIds, CancellationToken cancellationToken = default)
    {
        var retainedIds = retainedItemIds.Distinct().ToArray();

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // EF.Parameter binds the retained set as one JSON parameter, so this is safe for
        // arbitrarily large libraries.
        await db.DisabledItems
            .Where(e => !EF.Parameter(retainedIds).Contains(e.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.AnalyzedItems
            .Where(a => !EF.Parameter(retainedIds).Contains(a.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
