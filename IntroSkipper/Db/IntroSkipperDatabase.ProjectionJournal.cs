// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Projection-journal operations of <see cref="IntroSkipperDatabase"/>. Enqueueing
/// lives in <c>IntroSkipperDatabase.Changes.cs</c>, atomically with the mutation.
/// </summary>
internal sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbProjectionQueueItem>> GetProjectionQueueAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await db.ProjectionQueue.AsNoTracking()
            .OrderBy(q => q.ItemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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
