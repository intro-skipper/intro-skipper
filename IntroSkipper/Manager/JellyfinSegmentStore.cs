// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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

    // Must be declared before ProviderId: its initializer calls DeriveProviderId, and
    // static members initialize in textual order.
    private static readonly ConcurrentDictionary<string, string> _derivedProviderIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the provider id Jellyfin derives for Intro Skipper's segment provider.
    /// </summary>
    internal static string ProviderId { get; } = DeriveProviderId(Plugin.ProviderName);

    /// <summary>
    /// Derives the provider id Jellyfin computes for a segment provider name. Mirrors the
    /// server's MediaSegmentManager.GetProviderId: an MD5 (UTF-16) of the lower-cased name,
    /// via the same <see cref="MediaBrowser.Common.Extensions.BaseExtensions.GetMD5"/>
    /// extension the server uses, so the derivation cannot drift. The result is memoized
    /// because provider registrations are fixed for the process lifetime.
    /// </summary>
    /// <param name="providerName">The provider display name.</param>
    /// <returns>The derived provider id.</returns>
    internal static string DeriveProviderId(string providerName)
        => _derivedProviderIds.GetOrAdd(providerName, static name => name
            .ToLowerInvariant()
            .GetMD5()
            .ToString("N", CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public async Task ReplaceSegmentsAsync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var entities = segments.Select(segment => Map(segment, itemId)).ToList();

        await ReplaceScopeAsync(
            itemId,
            nameof(ReplaceSegmentsAsync),
            entities,
            db => OwnSegments(db, itemId),
            cancellationToken).ConfigureAwait(false);
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
    public Task ReplaceTypeAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return ReplaceEditableTypesAsync(itemId, [segment], [segment.Type], cancellationToken);
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
            await SaveExactlyAsync(db, itemId, nameof(CreateCommercialIfAbsentAsync), 1, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<JellyfinSegmentSnapshot?> GetSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.MediaSegments
                .AsNoTracking()
                .Where(segment => segment.ItemId == itemId && segment.Id == segmentId)
                .Select(segment => new JellyfinSegmentSnapshot(
                    segment.Id,
                    segment.ItemId,
                    segment.Type,
                    segment.StartTicks,
                    segment.EndTicks,
                    segment.SegmentProviderId))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // Deliberately not scoped to Intro Skipper's provider id: the editor lets
            // users remove any of the item's segments by id. It is scoped to the item,
            // unlike IMediaSegmentManager, so a caller holding a stale or mismatched
            // segment id can never delete another item's segment.
            await db.MediaSegments
                .Where(segment => segment.ItemId == itemId && segment.Id == segmentId)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ReplaceEditableTypesAsync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments, IReadOnlyCollection<MediaSegmentType> types, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(types);

        var typeArray = types.Distinct().ToArray();
        if (typeArray.Length == 0)
        {
            throw new ArgumentException("At least one segment type is required.", nameof(types));
        }

        foreach (var segment in segments)
        {
            if (!typeArray.Contains(segment.Type))
            {
                throw new ArgumentException($"Segment type '{segment.Type}' is not among the replaced types.", nameof(segments));
            }
        }

        var entities = segments.Select(segment => Map(segment, itemId)).ToList();

        // The editor owns "the" segments of the replaced types: existing entries are
        // removed regardless of which provider created them.
        await ReplaceScopeAsync(
            itemId,
            nameof(ReplaceEditableTypesAsync),
            entities,
            db => db.MediaSegments.Where(segment => segment.ItemId == itemId && typeArray.Contains(segment.Type)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared replace core: deletes the scoped rows and inserts the given entities. An
    /// empty entity set is a single atomic delete statement; otherwise delete and insert
    /// run in one write-first transaction.
    /// </summary>
    /// <param name="itemId">The item id being written, for diagnostics.</param>
    /// <param name="operation">The calling operation name, for diagnostics.</param>
    /// <param name="entities">The rows that should exist within the scope after the call.</param>
    /// <param name="deleteScope">Builds the query selecting the rows to replace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task ReplaceScopeAsync(
        Guid itemId,
        string operation,
        List<MediaSegment> entities,
        Func<JellyfinDbContext, IQueryable<MediaSegment>> deleteScope,
        CancellationToken cancellationToken)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            if (entities.Count == 0)
            {
                await deleteScope(db).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await RunWriteTransactionAsync(
                db,
                async () =>
                {
                    await deleteScope(db).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    db.MediaSegments.AddRange(entities);
                    await SaveExactlyAsync(db, itemId, operation, entities.Count, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JellyfinSegmentSnapshot>> GetItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.MediaSegments
                .AsNoTracking()
                .Where(segment => segment.ItemId == itemId)
                .OrderBy(segment => segment.StartTicks)
                .Select(segment => new JellyfinSegmentSnapshot(
                    segment.Id,
                    segment.ItemId,
                    segment.Type,
                    segment.StartTicks,
                    segment.EndTicks,
                    segment.SegmentProviderId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ItemSegmentCounts>> GetItemSegmentCountsAsync(CancellationToken cancellationToken)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // Whole-table group-by, materialized in one go. "Is this item still in the
            // library" is only answerable in-process, so the orphan filter cannot be pushed
            // into SQL and every distinct item id has to come back. The result set is one
            // row per item that has segments, not per segment and not per library item,
            // and this only runs on an explicit admin request. If that ever stops holding,
            // page the group-by by item id rather than filtering a bigger result in memory.
            return await db.MediaSegments
                .AsNoTracking()
                .GroupBy(segment => segment.ItemId)
                .Select(group => new ItemSegmentCounts(
                    group.Key,
                    group.Count(segment => segment.SegmentProviderId == ProviderId),
                    group.Count(segment => segment.SegmentProviderId != ProviderId)))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static IQueryable<MediaSegment> OwnSegments(JellyfinDbContext db, Guid itemId)
        => db.MediaSegments.Where(segment => segment.ItemId == itemId && segment.SegmentProviderId == ProviderId);

    private async Task SaveExactlyAsync(JellyfinDbContext db, Guid itemId, string operation, int expectedWrites, CancellationToken cancellationToken)
    {
        var written = await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (written != expectedWrites)
        {
            // Under Jellyfin's optimistic locking behavior SaveChanges failures are
            // captured by its retry policy instead of thrown and the context reports -1;
            // treat any mismatch as a failure so callers never commit or report a write
            // that did not happen.
            LogUnexpectedWriteCount(logger, operation, itemId, expectedWrites, written);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to roll back a Jellyfin media segment transaction.")]
    private static partial void LogRollbackFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Operation} for item {ItemId} expected to write {ExpectedWrites} media segment(s) but SaveChanges reported {Written}.")]
    private static partial void LogUnexpectedWriteCount(ILogger logger, string operation, Guid itemId, int expectedWrites, int written);
}
