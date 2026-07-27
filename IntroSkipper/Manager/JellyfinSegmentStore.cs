// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using IntroSkipper.Data;
using IntroSkipper.Helper;
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
    private static readonly ConcurrentDictionary<string, string> DerivedProviderIds = new(StringComparer.Ordinal);

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
        => DerivedProviderIds.GetOrAdd(providerName, static name => name
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
    public async Task CreateCommercialIfAbsentAsync(Guid itemId, MediaSegmentDto segment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var entity = Map(segment, itemId);
        var type = entity.Type;
        var startTicks = entity.StartTicks;
        var endTicks = entity.EndTicks;

        // Captured before Add: EF's key generator fills an empty Guid key in at Add time, so
        // after that point entity.Id no longer distinguishes a caller-supplied id from a
        // generated one. Only a supplied id can collide with an existing row.
        var suppliedId = entity.Id;

        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // Read-before-write on purpose: transactions here must start with a write so
            // SQLite takes its reserved lock immediately. This check is only a fast path:
            // the editor's per-item lock serializes in-process writers, but Jellyfin's own
            // writers use other connections and never take that lock, so an identical
            // commercial can still be committed between this check and the save. The
            // single-statement dedupe after the save is what makes the if-absent semantics
            // hold across connections.
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

            if (suppliedId != Guid.Empty
                && await db.MediaSegments
                    .AsNoTracking()
                    .AnyAsync(existing => existing.Id == suppliedId, cancellationToken)
                    .ConfigureAwait(false))
            {
                // The supplied id belongs to a different row, so the insert would violate
                // the primary key. Surface a client error instead of a constraint failure.
                throw new SegmentIdConflictException(suppliedId);
            }

            db.MediaSegments.Add(entity);
            try
            {
                await SaveExactlyAsync(db, itemId, nameof(CreateCommercialIfAbsentAsync), 1, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception saveException) when (suppliedId != Guid.Empty
                && saveException is DbUpdateException or InvalidOperationException)
            {
                // The pre-insert checks run outside the insert's own transaction (writes
                // here must begin with a write statement, see above), so a concurrent
                // writer can claim the supplied id between check and save. Re-check after
                // the failure so the race still surfaces as the typed client error; the
                // failed insert was never committed, so there is nothing to compensate.
                bool idTaken;
                try
                {
                    idTaken = await db.MediaSegments
                        .AsNoTracking()
                        .AnyAsync(existing => existing.Id == suppliedId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception recheckException) when (!recheckException.IsCritical())
                {
                    // A store that cannot answer the re-check is most likely the reason the
                    // save failed in the first place. Treat the id as unclaimed so the
                    // original failure, the actionable root cause, is what surfaces rather
                    // than being replaced by this secondary error.
                    LogConflictRecheckFailed(logger, recheckException, itemId);
                    idTaken = false;
                }

                if (idTaken)
                {
                    throw new SegmentIdConflictException(suppliedId);
                }

                throw;
            }

            // The insert is committed, so a competing connection may have committed an
            // identical row inside the check-then-insert window above. If-absent semantics:
            // that row wins, so this call's own row is deleted again. Equivalence check and
            // delete run as one statement, so no writer can interleave between them, and
            // the statement does not honor cancellation because the committed insert must
            // be reconciled deterministically once it exists.
            var insertedId = entity.Id;
            try
            {
                var discarded = await db.MediaSegments
                    .Where(own => own.Id == insertedId
                        && db.MediaSegments.Any(other => other.Id != insertedId
                            && other.ItemId == itemId
                            && other.Type == type
                            && other.StartTicks == startTicks
                            && other.EndTicks == endTicks))
                    .ExecuteDeleteAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                if (discarded > 0)
                {
                    LogDiscardedRacedDuplicate(logger, itemId);
                }
            }
            catch (Exception reconcileException) when (!reconcileException.IsCritical())
            {
                // The insert committed, so the call succeeded; throwing here would make
                // the editor compensate away the plugin row of a Jellyfin row that
                // exists, tearing the stores apart. A duplicate that survives a failed
                // reconcile is benign and short-lived: the next refresh replaces Intro
                // Skipper's rows for the item authoritatively from the plugin database.
                LogDuplicateReconcileFailed(logger, reconcileException, itemId);
            }
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
    public async Task<bool> SegmentIdExistsAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            // Deliberately unscoped by item: the point is to find the id wherever it lives.
            return await db.MediaSegments
                .AsNoTracking()
                .AnyAsync(segment => segment.Id == segmentId, cancellationToken)
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
        // removed regardless of which provider created them. Caller-supplied ids are
        // checked against rows outside the replaced scope so a collision surfaces as a
        // client error instead of a primary-key constraint failure.
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
    /// <exception cref="SegmentIdConflictException">A supplied segment id already identifies a row outside the replaced scope.</exception>
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

            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                try
                {
                    await deleteScope(db).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

                    // The conflict check runs after the scoped delete so reusing an id from
                    // inside the scope is allowed; only a row outside it is a real collision.
                    // The refresh path supplies no ids, so this is a cheap no-op there.
                    await ThrowOnSegmentIdConflictAsync(db, entities, cancellationToken).ConfigureAwait(false);

                    db.MediaSegments.AddRange(entities);
                    await SaveExactlyAsync(db, itemId, operation, entities.Count, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Fails a replace whose supplied ids collide with rows outside the replaced scope.
    /// Runs after the scoped delete in the same transaction, so any row still holding a
    /// supplied id would violate the primary key on insert. Surfacing the conflict as a
    /// typed exception lets the API report a client error instead of a constraint failure.
    /// </summary>
    private static async Task ThrowOnSegmentIdConflictAsync(JellyfinDbContext db, List<MediaSegment> entities, CancellationToken cancellationToken)
    {
        var suppliedIds = entities
            .Where(entity => entity.Id != Guid.Empty)
            .Select(entity => entity.Id)
            .ToArray();

        if (suppliedIds.Length == 0)
        {
            return;
        }

        var conflict = await db.MediaSegments
            .AsNoTracking()
            .Where(segment => suppliedIds.Contains(segment.Id))
            .Select(segment => (Guid?)segment.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (conflict is Guid conflictingId)
        {
            throw new SegmentIdConflictException(conflictingId);
        }
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not re-check the supplied media segment id for item {ItemId} after a failed insert; reporting the original failure.")]
    private static partial void LogConflictRecheckFailed(ILogger logger, Exception ex, Guid itemId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discarded a commercial insert for item {ItemId} because an identical row was committed concurrently.")]
    private static partial void LogDiscardedRacedDuplicate(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not reconcile a possibly duplicated commercial for item {ItemId} after its insert committed; a surviving duplicate is replaced by the next segment refresh.")]
    private static partial void LogDuplicateReconcileFailed(ILogger logger, Exception ex, Guid itemId);
}
