// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Db;

namespace IntroSkipper.Repositories;

/// <summary>
/// Repository interface for outbox data access.
/// </summary>
public interface IOutboxRepository
{
    /// <summary>
    /// Adds a new outbox entry.
    /// </summary>
    /// <param name="entry">The outbox entry to add to the repository.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The created <see cref="DbSegmentOutbox"/> wrapped in a <see cref="Task"/>.</returns>
    Task<DbSegmentOutbox> AddAsync(DbSegmentOutbox entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims pending outbox entries for processing.
    /// Uses atomic update to prevent concurrent processing by multiple instances.
    /// </summary>
    /// <param name="instanceId">Unique identifier for this processor instance.</param>
    /// <param name="limit">Maximum number of entries to claim.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task containing a read-only list of claimed <see cref="DbSegmentOutbox"/> entries.</returns>
    Task<IReadOnlyList<DbSegmentOutbox>> ClaimPendingAsync(string instanceId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks multiple outbox entries as processed in a single operation.
    /// </summary>
    /// <param name="ids">The identifiers of the outbox entries to mark as processed.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task MarkProcessedBatchAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the retry count for multiple entries and releases their claims.
    /// </summary>
    /// <param name="ids">The identifiers of the entries to increment retry counts for.</param>
    /// <param name="errorMessage">A descriptive error message from the failed processing attempt.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task IncrementRetryBatchAsync(IEnumerable<int> ids, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases stale claims that have exceeded the claim timeout.
    /// </summary>
    /// <param name="claimTimeout">Entries claimed longer than this duration are released.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ReleaseStaleClaimsAsync(TimeSpan claimTimeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes old processed outbox entries.
    /// </summary>
    /// <param name="olderThan">Delete any processed outbox entries with a timestamp older than this value.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task DeleteOldEntriesAsync(DateTime olderThan, CancellationToken cancellationToken = default);
}
