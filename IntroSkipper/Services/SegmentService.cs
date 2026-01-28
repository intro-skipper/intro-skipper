// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Repositories;

namespace IntroSkipper.Services;

/// <summary>
/// Service implementation for segment business logic.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SegmentService"/> class.
/// </remarks>
/// <param name="segmentRepository">Repository for segment persistence operations.</param>
/// <param name="outboxRepository">Repository for outbox messages used for sync operations.</param>
/// <param name="dbContext">Database context used for transactions.</param>
public class SegmentService(
    ISegmentRepository segmentRepository,
    IOutboxRepository outboxRepository,
    IntroSkipperDbContext dbContext) : ISegmentService
{
    private readonly ISegmentRepository _segmentRepository = segmentRepository;
    private readonly IOutboxRepository _outboxRepository = outboxRepository;
    private readonly IntroSkipperDbContext _dbContext = dbContext;

    /// <inheritdoc/>
    public async Task<DbSegment> CreateSegmentAsync(Segment segment, AnalysisMode type, bool isFirstAppearance = false, CancellationToken cancellationToken = default)
    {
        DbSegment? result = null;

        await ExecuteInTransactionAsync(
            async () =>
            {
                var dbSegment = new DbSegment(segment, type, isFirstAppearance);
                result = await _segmentRepository.AddAsync(dbSegment, cancellationToken).ConfigureAwait(false);

                await QueueOutboxEntryAsync(segment.EpisodeId, OutboxOperation.Upsert, result.Id, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return result!;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        return await _segmentRepository.GetByItemIdAsync(itemId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetSegmentsByTypeAsync(Guid itemId, AnalysisMode type, CancellationToken cancellationToken = default)
    {
        return await _segmentRepository.GetByItemIdAndTypeAsync(itemId, type, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetSegmentsDictionaryAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var segments = await GetSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);

        // Group by type and take the first segment of each type for backward compatibility
        return segments
            .GroupBy(s => s.Type)
            .ToDictionary(g => g.Key, g => g.First().ToSegment());
    }

    /// <inheritdoc/>
    public async Task UpdateSegmentAsync(DbSegment segment, CancellationToken cancellationToken = default)
    {
        await ExecuteInTransactionAsync(
            async () =>
            {
                await _segmentRepository.UpdateAsync(segment, cancellationToken).ConfigureAwait(false);
                await QueueOutboxEntryAsync(segment.ItemId, OutboxOperation.Upsert, segment.Id, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteSegmentAsync(int segmentId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await ExecuteInTransactionAsync(
            async () =>
            {
                await _segmentRepository.DeleteAsync(segmentId, cancellationToken).ConfigureAwait(false);

                // SegmentId is null for delete operations because the segment no longer exists.
                // The outbox processor will trigger a full refresh from the provider for this item.
                await QueueOutboxEntryAsync(itemId, OutboxOperation.Delete, segmentId: null, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteSegmentsByTypeAsync(Guid itemId, AnalysisMode type, CancellationToken cancellationToken = default)
    {
        await ExecuteInTransactionAsync(
            async () =>
            {
                await _segmentRepository.DeleteByItemIdAndTypeAsync(itemId, type, cancellationToken).ConfigureAwait(false);

                // SegmentId is null for delete operations because the segment no longer exists.
                // The outbox processor will trigger a full refresh from the provider for this item.
                await QueueOutboxEntryAsync(itemId, OutboxOperation.Delete, segmentId: null, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteAllSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await ExecuteInTransactionAsync(
            async () =>
            {
                await _segmentRepository.DeleteByItemIdAsync(itemId, cancellationToken).ConfigureAwait(false);

                // SegmentId is null for delete operations because the segment no longer exists.
                // The outbox processor will trigger a full refresh from the provider for this item.
                await QueueOutboxEntryAsync(itemId, OutboxOperation.Delete, segmentId: null, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteSeasonSegmentsAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await ExecuteInTransactionAsync(
            async () =>
            {
                // Get all affected item IDs before deleting so we can queue outbox entries
                var segments = await _segmentRepository.GetBySeasonIdAsync(seasonId, cancellationToken).ConfigureAwait(false);
                var affectedItemIds = segments.Select(s => s.ItemId).Distinct().ToList();

                // Delete all segments for the season
                await _segmentRepository.DeleteBySeasonIdAsync(seasonId, cancellationToken).ConfigureAwait(false);

                // Queue outbox entries for each affected item
                foreach (var itemId in affectedItemIds)
                {
                    await QueueOutboxEntryAsync(itemId, OutboxOperation.Delete, segmentId: null, cancellationToken).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CleanupOrphanedSegmentsAsync(IEnumerable<Guid> validItemIds, CancellationToken cancellationToken = default)
    {
        await _segmentRepository.DeleteOrphanedSegmentsAsync(validItemIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the specified action within a database transaction.
    /// Commits on success, rolls back on failure.
    /// </summary>
    /// <param name="action">The async action to execute within the transaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                await action().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// Queues an outbox entry for synchronization to Jellyfin.
    /// </summary>
    /// <param name="itemId">The item ID associated with the segment.</param>
    /// <param name="operation">The type of operation (Upsert or Delete).</param>
    /// <param name="segmentId">The segment ID (null for delete operations).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task QueueOutboxEntryAsync(Guid itemId, OutboxOperation operation, int? segmentId, CancellationToken cancellationToken)
    {
        await _outboxRepository.AddAsync(
            new DbSegmentOutbox
            {
                ItemId = itemId,
                Operation = operation,
                SegmentId = segmentId
            },
            cancellationToken).ConfigureAwait(false);
    }
}
