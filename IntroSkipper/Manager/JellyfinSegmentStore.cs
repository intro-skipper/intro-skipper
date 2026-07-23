// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Reads and writes Intro Skipper's media segments directly in Jellyfin's database
/// through the server's own pooled context factory, so writes participate in whatever
/// locking behavior the server has configured. Multi-statement writes are transactional
/// and, unless documented otherwise, scoped to Intro Skipper's provider id.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="JellyfinSegmentStore"/> class.
/// </remarks>
/// <param name="contextFactory">The server's Jellyfin database context factory.</param>
/// <param name="logger">Application logger.</param>
public sealed partial class JellyfinSegmentStore(
    IDbContextFactory<JellyfinDbContext> contextFactory,
    ILogger<JellyfinSegmentStore> logger) : IJellyfinSegmentStore
{
    private const int DeleteChunkSize = 500;

    /// <summary>
    /// Gets the provider id Jellyfin derives for Intro Skipper's segment provider.
    /// Mirrors the server's MediaSegmentManager.GetProviderId: an MD5 (UTF-16) of the
    /// lower-cased provider name, via the same <see cref="MediaBrowser.Common.Extensions.BaseExtensions.GetMD5"/>
    /// extension the server uses, so the derivation can never drift.
    /// </summary>
    internal static string ProviderId { get; } = Plugin.ProviderName
        .ToLowerInvariant()
        .GetMD5()
        .ToString("N", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async Task ReplaceSegmentsAsync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var entities = segments.Select(segment => Map(segment, itemId)).ToList();

        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            if (entities.Count == 0)
            {
                await OwnSegments(db, itemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await RunWriteTransactionAsync(
                db,
                async () =>
                {
                    await OwnSegments(db, itemId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    db.MediaSegments.AddRange(entities);
                    await SaveExactlyAsync(db, entities.Count, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteOwnSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Where(static id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            foreach (var chunk in ids.Chunk(DeleteChunkSize))
            {
                await db.MediaSegments
                    .Where(segment => chunk.Contains(segment.ItemId) && segment.SegmentProviderId == ProviderId)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task ReplaceTypeAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var entity = Map(segment, itemId);
        var type = entity.Type;

        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await RunWriteTransactionAsync(
                db,
                async () =>
                {
                    // The editor owns "the" segment of a type: existing entries of the
                    // same type are removed regardless of which provider created them.
                    await db.MediaSegments
                        .Where(existing => existing.ItemId == itemId && existing.Type == type)
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    db.MediaSegments.Add(entity);
                    await SaveExactlyAsync(db, 1, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task CreateCommercialIfAbsentAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var entity = Map(segment, itemId);
        var type = entity.Type;
        var startTicks = entity.StartTicks;
        var endTicks = entity.EndTicks;

        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // Read-before-write on purpose: transactions here must start with a write so
            // SQLite takes its reserved lock immediately. The tiny check-then-insert
            // window matches the previous IMediaSegmentManager-based behavior and is
            // serialized in-process by the editor's per-item lock.
            var exists = await db.MediaSegments
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.ItemId == itemId
                        && existing.Type == type
                        && existing.StartTicks == startTicks
                        && existing.EndTicks == endTicks,
                    cancellationToken)
                .ConfigureAwait(false);

            if (exists)
            {
                return;
            }

            db.MediaSegments.Add(entity);
            await SaveExactlyAsync(db, 1, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<MediaSegmentDto?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var entity = await db.MediaSegments
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    segment => segment.ItemId == itemId && segment.Id == segmentId,
                    cancellationToken)
                .ConfigureAwait(false);

            return entity is null ? null : Map(entity);
        }
    }

    /// <inheritdoc />
    public async Task DeleteSegmentAsync(Guid segmentId)
    {
        var db = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // Deliberately not scoped to Intro Skipper's provider id: the editor lets
            // users remove any segment by id, matching IMediaSegmentManager semantics.
            await db.MediaSegments
                .Where(segment => segment.Id == segmentId)
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);
        }
    }

    private static IQueryable<MediaSegment> OwnSegments(JellyfinDbContext db, Guid itemId)
        => db.MediaSegments.Where(segment => segment.ItemId == itemId && segment.SegmentProviderId == ProviderId);

    private static async Task SaveExactlyAsync(JellyfinDbContext db, int expectedWrites, CancellationToken cancellationToken)
    {
        var written = await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (written != expectedWrites)
        {
            // Under Jellyfin's optimistic locking behavior SaveChanges failures are
            // captured by its retry policy instead of thrown and the context reports -1;
            // treat any mismatch as a failure so callers never commit or report a write
            // that did not happen.
            throw new InvalidOperationException(FormattableString.Invariant(
                $"Expected to write {expectedWrites} media segment(s) but SaveChanges reported {written}."));
        }
    }

    private async Task RunWriteTransactionAsync(JellyfinDbContext db, Func<Task> writes, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                await writes().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // EF raises no interceptor event on transaction dispose, so without an
                // explicit rollback Jellyfin's pessimistic locking behavior would never
                // release its process-wide write lock. A failed commit already released
                // it; the guard swallows the double-release from the follow-up rollback.
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    LogRollbackFailed(logger, rollbackException);
                }

                throw;
            }
        }
    }

    private static MediaSegment Map(MediaSegmentDto segment, Guid itemId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segment.EndTicks, segment.StartTicks);

        return new MediaSegment
        {
            Id = segment.Id,
            ItemId = itemId,
            StartTicks = segment.StartTicks,
            EndTicks = segment.EndTicks,
            Type = segment.Type,
            SegmentProviderId = ProviderId
        };
    }

    private static MediaSegmentDto Map(MediaSegment segment)
        => new()
        {
            Id = segment.Id,
            ItemId = segment.ItemId,
            StartTicks = segment.StartTicks,
            EndTicks = segment.EndTicks,
            Type = segment.Type
        };

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to roll back a Jellyfin media segment transaction.")]
    private static partial void LogRollbackFailed(ILogger logger, Exception ex);
}
