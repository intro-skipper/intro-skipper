// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// The projection-journal surface of the segment database, consumed by the durable
/// segment-change coordinator: read pending work, complete it, or record a failed
/// attempt. Work is enqueued only inside the facade — by
/// <see cref="IIntroSkipperDatabase.ApplyChangeAsync"/> and by the analyzer and
/// maintenance writes that change servable state — always atomically with the
/// mutation.
/// </summary>
internal interface ISegmentProjectionJournal
{
    /// <summary>
    /// Reads the pending queue rows, ordered by item id: all of them, or one item's.
    /// Items without a row have no pending work.
    /// </summary>
    /// <param name="itemId">Item filter, or <see langword="null"/> for every row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Untracked queue rows.</returns>
    Task<IReadOnlyList<DbProjectionQueueItem>> GetProjectionQueueAsync(Guid? itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the ids of items whose work is due: no backoff recorded, or the backoff
    /// has elapsed.
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The due item ids.</returns>
    Task<IReadOnlyList<Guid>> GetDueProjectionItemIdsAsync(DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one item's pending work: the queue row plus its journaled foreign-row
    /// deletes in FIFO order.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The work, or <see langword="null"/> when nothing is pending.</returns>
    Task<ProjectionWork?> ReadProjectionWorkAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Completes applied work: deletes the processed operations and the queue row in
    /// one transaction, so both commit or both roll back. The queue row is deleted
    /// only when its version still matches, so work enqueued while the apply was in
    /// flight survives.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="version">The queue-row version the caller projected.</param>
    /// <param name="processedOperationIds">Ids of the operations the apply processed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the queue row was retired at the projected
    /// version; <see langword="false"/> when it survived because newer work superseded
    /// the version mid-apply, so the item is still behind.</returns>
    Task<bool> CompleteProjectionWorkAsync(Guid itemId, long version, IReadOnlyList<long> processedOperationIds, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt on the item's queue row: increments the attempt count
    /// and stores the backoff due time and sanitized failure. Guarded by the version
    /// the failed attempt projected, like the completion: a no-op when the row is
    /// gone (the work completed concurrently) or superseded (a newer enqueue made the
    /// work due immediately — its supersession must not be stomped with a stale
    /// backoff).
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="version">The queue-row version the failed attempt projected.</param>
    /// <param name="nextAttemptAt">UTC time the next attempt is due.</param>
    /// <param name="failure">Sanitized failure message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RecordProjectionFailureAsync(Guid itemId, long version, DateTime nextAttemptAt, string failure, CancellationToken cancellationToken);
}
