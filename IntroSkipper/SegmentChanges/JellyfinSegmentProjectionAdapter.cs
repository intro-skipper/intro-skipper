// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Manager;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.SegmentChanges;

/// <summary>Applies durable item plans directly to Jellyfin's media segment table.</summary>
internal sealed partial class JellyfinSegmentProjectionAdapter(
    IDbContextFactory<JellyfinDbContext> contextFactory,
    ILogger<JellyfinSegmentProjectionAdapter> logger) : ISegmentProjectionAdapter
{
    /// <inheritdoc />
    public async Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var row = await db.MediaSegments.AsNoTracking()
                .FirstOrDefaultAsync(segment => segment.Id == externalSegmentId, cancellationToken)
                .ConfigureAwait(false);
            return row is null ? null : new ExternalSegmentTarget(row.Id, row.ItemId, row.Type, row.StartTicks, row.EndTicks);
        }
    }

    /// <inheritdoc />
    public async Task ApplyAsync(SegmentProjectionPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var entities = plan.Segments.Select(segment => new MediaSegment
        {
            Id = segment.Id,
            ItemId = plan.ItemId,
            Type = segment.Type,
            StartTicks = segment.StartTicks,
            EndTicks = segment.EndTicks,
            SegmentProviderId = JellyfinSegmentStore.ProviderId
        }).ToList();

        if (entities.Any(segment => segment.Id == Guid.Empty || segment.EndTicks <= segment.StartTicks))
        {
            throw new InvalidOperationException("Projection plans must contain stable IDs and valid tick ranges.");
        }

        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                try
                {
                    foreach (var operation in plan.ExternalOperations)
                    {
                        if (operation.Kind != ProjectionExternalOperationKind.Delete)
                        {
                            throw new InvalidOperationException("Unsupported projection operation.");
                        }

                        var deleted = await db.MediaSegments
                            .Where(segment => segment.Id == operation.ExternalSegmentId
                                && segment.ItemId == plan.ItemId
                                && segment.Type == operation.ExpectedType)
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (deleted == 0)
                        {
                            var mismatched = await db.MediaSegments.AsNoTracking()
                                .AnyAsync(segment => segment.Id == operation.ExternalSegmentId, cancellationToken)
                                .ConfigureAwait(false);
                            if (mismatched)
                            {
                                throw new InvalidOperationException("The external segment no longer matches its validated owner and type.");
                            }
                        }
                    }

                    await db.MediaSegments
                        .Where(segment => segment.ItemId == plan.ItemId && segment.SegmentProviderId == JellyfinSegmentStore.ProviderId)
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (entities.Count > 0)
                    {
                        db.MediaSegments.AddRange(entities);
                        var written = await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        if (written != entities.Count)
                        {
                            LogUnexpectedWriteCount(logger, plan.ItemId, entities.Count, written);
                            throw new InvalidOperationException($"Expected {entities.Count} projection writes but Jellyfin reported {written}.");
                        }
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogRollbackFailed(logger, ex);
                    }

                    throw;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Projection for item {ItemId} expected {ExpectedWrites} writes but Jellyfin reported {Written}.")]
    private static partial void LogUnexpectedWriteCount(ILogger logger, Guid itemId, int expectedWrites, int written);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to roll back a Jellyfin projection transaction.")]
    private static partial void LogRollbackFailed(ILogger logger, Exception exception);
}
