// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Projection-journal operations of <see cref="IntroSkipperDatabase"/>. Enqueueing
/// lives in <c>IntroSkipperDatabase.Changes.cs</c>, atomically with the mutation.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <summary>
    /// Reads every pending queue row, ordered by item id. Items without a row have no
    /// pending work; one item's work is read through <see cref="ReadProjectionWorkAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Untracked queue rows.</returns>
    public async Task<IReadOnlyList<DbProjectionQueueItem>> GetProjectionQueueAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await db.ProjectionQueue.AsNoTracking()
            .OrderBy(q => q.ItemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the ids of items whose work is due: no backoff recorded, or the backoff
    /// has elapsed.
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The due item ids.</returns>
    public async Task<IReadOnlyList<Guid>> GetDueProjectionItemIdsAsync(DateTime now, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await db.ProjectionQueue.AsNoTracking()
            .Where(q => q.NextAttemptAt == null || q.NextAttemptAt <= now)
            .OrderBy(q => q.ItemId)
            .Select(q => q.ItemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one item's pending work: the untracked queue row plus its journaled
    /// foreign-row deletes in FIFO order.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The work, or <see langword="null"/> when nothing is pending.</returns>
    public async Task<(DbProjectionQueueItem Item, IReadOnlyList<DbProjectionExternalOperation> Operations)?> ReadProjectionWorkAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var item = await db.ProjectionQueue.AsNoTracking()
            .FirstOrDefaultAsync(q => q.ItemId == itemId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var operations = await db.ProjectionExternalOperations.AsNoTracking()
            .Where(o => o.ItemId == itemId)
            .OrderBy(o => o.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (item, operations);
    }

    /// <summary>
    /// Completes applied work: deletes the processed operations, then the queue row,
    /// the latter only when its version still matches, so work enqueued while the
    /// apply was in flight survives. The two deletes are separate statements on
    /// purpose: a crash between them leaves the row, which costs one extra idempotent
    /// re-sync.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="version">The queue-row version the caller projected.</param>
    /// <param name="processedOperationIds">Ids of the operations the apply processed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the queue row was retired at the projected
    /// version; <see langword="false"/> when it survived because newer work superseded
    /// the version mid-apply, so the item is still behind.</returns>
    public async Task<bool> CompleteProjectionWorkAsync(Guid itemId, long version, IReadOnlyList<long> processedOperationIds, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        if (processedOperationIds.Count > 0)
        {
            await db.ProjectionExternalOperations
                .Where(o => o.ItemId == itemId && EF.Parameter(processedOperationIds).Contains(o.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return await db.ProjectionQueue
            .Where(q => q.ItemId == itemId && q.Version == version)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Records a failed attempt on the item's queue row: increments the attempt count
    /// and stores the backoff due time and sanitized failure. Guarded by the version
    /// the failed attempt projected, like the completion: a no-op when the row is
    /// gone (the work completed concurrently) or superseded (a newer enqueue made the
    /// work due immediately, and that must not be stomped with a stale backoff).
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="version">The queue-row version the failed attempt projected.</param>
    /// <param name="nextAttemptAt">UTC time the next attempt is due.</param>
    /// <param name="failure">Sanitized failure message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task RecordProjectionFailureAsync(Guid itemId, long version, DateTime nextAttemptAt, string failure, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        await db.ProjectionQueue
            .Where(q => q.ItemId == itemId && q.Version == version)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(q => q.AttemptCount, q => q.AttemptCount + 1)
                    .SetProperty(q => q.NextAttemptAt, nextAttemptAt)
                    .SetProperty(q => q.Failure, failure),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
