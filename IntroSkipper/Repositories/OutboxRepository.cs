// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Repositories;

/// <summary>
/// Repository implementation for outbox data access.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="OutboxRepository"/> class.
/// </remarks>
/// <param name="dbContext">Database context.</param>
public class OutboxRepository(IntroSkipperDbContext dbContext) : IOutboxRepository
{
    private readonly IntroSkipperDbContext _dbContext = dbContext;

    /// <inheritdoc/>
    public async Task<DbSegmentOutbox> AddAsync(DbSegmentOutbox entry, CancellationToken cancellationToken = default)
    {
        entry.CreatedAt = DateTime.UtcNow;
        _dbContext.DbSegmentOutbox.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegmentOutbox>> ClaimPendingAsync(string instanceId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        var now = DateTime.UtcNow;

        // Use a transaction to ensure atomic claim-and-return operation.
        // This prevents race conditions between multiple processor instances.
        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                // Find and claim in a single atomic operation within the transaction
                var pendingEntries = await _dbContext.DbSegmentOutbox
                    .Where(e => e.ProcessedAt == null
                        && e.ClaimedBy == null
                        && e.RetryCount < OutboxConstants.MaxRetryCount)
                    .OrderBy(e => e.CreatedAt)
                    .Take(limit)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (pendingEntries.Count == 0)
                {
                    return Array.Empty<DbSegmentOutbox>();
                }

                // Claim the entries within the same transaction
                foreach (var entry in pendingEntries)
                {
                    entry.ClaimedBy = instanceId;
                    entry.ClaimedAt = now;
                }

                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return pendingEntries;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public async Task MarkProcessedBatchAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        await _dbContext.DbSegmentOutbox
            .Where(e => idList.Contains(e.Id))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.ProcessedAt, DateTime.UtcNow)
                    .SetProperty(e => e.ClaimedBy, (string?)null)
                    .SetProperty(e => e.ClaimedAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task IncrementRetryBatchAsync(IEnumerable<int> ids, string errorMessage, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        // Use ExecuteUpdateAsync for efficient batch update without loading entities
        await _dbContext.DbSegmentOutbox
            .Where(e => idList.Contains(e.Id))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.RetryCount, e => e.RetryCount + 1)
                    .SetProperty(e => e.ErrorMessage, errorMessage)
                    .SetProperty(e => e.ClaimedBy, (string?)null)
                    .SetProperty(e => e.ClaimedAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ReleaseStaleClaimsAsync(TimeSpan claimTimeout, CancellationToken cancellationToken = default)
    {
        var staleThreshold = DateTime.UtcNow - claimTimeout;

        await _dbContext.DbSegmentOutbox
            .Where(e => e.ClaimedAt != null && e.ClaimedAt < staleThreshold && e.ProcessedAt == null)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(e => e.ClaimedBy, (string?)null)
                    .SetProperty(e => e.ClaimedAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteOldEntriesAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        await _dbContext.DbSegmentOutbox
            .Where(e => e.ProcessedAt != null && e.ProcessedAt < olderThan)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
