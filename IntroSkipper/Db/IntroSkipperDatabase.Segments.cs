// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Segment (<see cref="DbSegment"/>) operations of <see cref="IntroSkipperDatabase"/>.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <inheritdoc/>
    public async Task<int> ReplaceAutoSegmentsAsync(
        Guid itemId,
        AnalysisMode mode,
        IReadOnlyList<Segment> segments,
        SegmentSource source,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (source == SegmentSource.User)
        {
            throw new ArgumentException("Analysis writes must not use the User source.", nameof(source));
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        try
        {
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var existing = await db.Segments
                    .Where(s => s.ItemId == itemId && s.Type == mode)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var tombstones = existing.Where(s => s.State == SegmentState.Suppressed).ToList();
                var userRows = existing.Where(s => s.State == SegmentState.Active && s.Source == SegmentSource.User).ToList();
                var autoRows = existing.Where(s => s.State == SegmentState.Active && s.Source != SegmentSource.User).ToList();

                var intros = mode == AnalysisMode.Credits
                    ? await db.Segments
                        .AsNoTracking()
                        .Where(s => s.ItemId == itemId && s.Type == AnalysisMode.Introduction && s.State == SegmentState.Active)
                        .Select(s => new { s.StartTicks, s.EndTicks })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false)
                    : [];

                var accepted = new List<DbSegment>();
                foreach (var segment in segments.OrderBy(s => s.Start))
                {
                    if (!TickConversions.TryFromSecondsRange(segment.Start, segment.End, out var startTicks, out var endTicks))
                    {
                        continue;
                    }

                    if (tombstones.Any(t => AutoSegmentAdmissionPolicy.Overlaps(startTicks, endTicks, t.StartTicks, t.EndTicks)))
                    {
                        LogAutoSegmentSuppressedByTombstone(_logger, mode, itemId);
                        continue;
                    }

                    if (userRows.Any(u => AutoSegmentAdmissionPolicy.Overlaps(startTicks, endTicks, u.StartTicks, u.EndTicks)))
                    {
                        LogAutoSegmentSkippedForUserOverlap(_logger, mode, itemId);
                        continue;
                    }

                    if (mode == AnalysisMode.Credits
                        && intros.Any(i => AutoSegmentAdmissionPolicy.Overlaps(startTicks, endTicks, i.StartTicks, i.EndTicks)))
                    {
                        LogCreditsOverlapWithIntro(_logger, itemId);
                        continue;
                    }

                    if (accepted.Any(a => a.StartTicks == startTicks && a.EndTicks == endTicks))
                    {
                        continue;
                    }

                    accepted.Add(new DbSegment(itemId, mode, startTicks, endTicks, source, configHash));
                }

                // Keep automatic rows whose boundaries are unchanged so their ids stay
                // stable across re-analysis (Jellyfin rows keep the same Guids); replace
                // the rest.
                var kept = 0;
                foreach (var row in autoRows)
                {
                    var match = accepted.Find(a => a.StartTicks == row.StartTicks && a.EndTicks == row.EndTicks);
                    if (match is not null)
                    {
                        accepted.Remove(match);
                        row.Source = source;
                        row.ConfigHash = configHash;
                        kept++;
                    }
                    else
                    {
                        db.Segments.Remove(row);
                    }
                }

                db.Segments.AddRange(accepted);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return kept + accepted.Count;
            }
        }
        catch (Exception ex)
        {
            LogFailedToUpdateSegments(_logger, ex, itemId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<DbSegment> AddUserSegmentAsync(
        Guid itemId,
        AnalysisMode mode,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startTicks, endTicks);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var exact = await db.Segments
            .FirstOrDefaultAsync(
                s => s.ItemId == itemId && s.Type == mode && s.StartTicks == startTicks && s.EndTicks == endTicks,
                cancellationToken)
            .ConfigureAwait(false);

        if (exact is not null)
        {
            // Promote an automatic row, revive a tombstone, or return the user row unchanged.
            exact.State = SegmentState.Active;
            exact.Source = SegmentSource.User;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return exact;
        }

        var row = new DbSegment(itemId, mode, startTicks, endTicks, SegmentSource.User);
        db.Segments.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc/>
    public async Task<DbSegment> ReplaceUserSegmentAsync(
        Guid itemId,
        AnalysisMode mode,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startTicks, endTicks);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var existing = await db.Segments
                .Where(s => s.ItemId == itemId && s.Type == mode)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            db.Segments.RemoveRange(existing.Where(s => s.State == SegmentState.Active));

            // A tombstone with exactly the new range would collide on the unique index:
            // revive it as the user segment instead of inserting.
            var row = existing.Find(s => s.State == SegmentState.Suppressed && s.StartTicks == startTicks && s.EndTicks == endTicks);
            if (row is not null)
            {
                row.State = SegmentState.Active;
                row.Source = SegmentSource.User;
            }
            else
            {
                row = new DbSegment(itemId, mode, startTicks, endTicks, SegmentSource.User);
                db.Segments.Add(row);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return row;
        }
    }

    /// <inheritdoc/>
    public async Task<DbSegment?> UpdateSegmentAsync(
        Guid itemId,
        Guid segmentId,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startTicks, endTicks);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var row = await db.Segments
            .FirstOrDefaultAsync(s => s.ItemId == itemId && s.Id == segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.State == SegmentState.Suppressed)
        {
            return null;
        }

        var occupant = await db.Segments
            .FirstOrDefaultAsync(
                s => s.Id != segmentId
                    && s.ItemId == row.ItemId
                    && s.Type == row.Type
                    && s.StartTicks == startTicks
                    && s.EndTicks == endTicks,
                cancellationToken)
            .ConfigureAwait(false);
        if (occupant is not null)
        {
            if (occupant.State != SegmentState.Suppressed)
            {
                // The user explicitly claims an exactly-occupied active range: like
                // AddUserSegmentAsync's in-place promotion, the occupant (whose id
                // Jellyfin already knows) survives as the user segment and the moved
                // row is absorbed into it.
                db.Segments.Remove(row);
                occupant.Source = SegmentSource.User;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return occupant;
            }

            // The user explicitly reclaims a previously deleted range: absorb the
            // tombstone so the unique index cannot fire. Its protective purpose is
            // preserved because the occupying row becomes user-provided, which
            // analysis never overwrites.
            db.Segments.Remove(occupant);
        }

        row.StartTicks = startTicks;
        row.EndTicks = endTicks;
        row.Source = SegmentSource.User;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc/>
    public async Task<DbSegment?> DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var row = await db.Segments
            .FirstOrDefaultAsync(s => s.ItemId == itemId && s.Id == segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.State == SegmentState.Suppressed)
        {
            return null;
        }

        var snapshot = row.Clone();
        if (row.Source == SegmentSource.User)
        {
            db.Segments.Remove(row);
        }
        else
        {
            row.State = SegmentState.Suppressed;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <inheritdoc/>
    public async Task UndoDeleteAsync(DbSegment? deletedSnapshot, CancellationToken cancellationToken = default)
    {
        if (deletedSnapshot is null)
        {
            return;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var row = await db.Segments
            .FirstOrDefaultAsync(s => s.Id == deletedSnapshot.Id, cancellationToken)
            .ConfigureAwait(false);

        if (row is not null)
        {
            row.State = deletedSnapshot.State;
            row.Source = deletedSnapshot.Source;
        }
        else
        {
            db.Segments.Add(deletedSnapshot.Clone());
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // An equivalent row appeared in the meantime — the restore's intent is met.
        }
    }

    /// <inheritdoc/>
    public async Task<DbSegment?> RestoreSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var row = await db.Segments
            .FirstOrDefaultAsync(s => s.ItemId == itemId && s.Id == segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.State != SegmentState.Suppressed)
        {
            return null;
        }

        row.State = SegmentState.Active;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <inheritdoc/>
    public async Task<DbSegment?> GetSegmentAsync(Guid segmentId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await db.Segments
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == segmentId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(
        Guid itemId,
        bool includeSuppressed = false,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        return await OrderedItemSegments(db, itemId, includeSuppressed)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetServableSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // The cross-set Any translates to a NOT EXISTS probe inside the segment
        // query, so the disable policy costs no second roundtrip on the
        // per-playback and provider read paths.
        return await OrderedItemSegments(db, itemId, includeSuppressed: false)
            .Where(s => s.Source == SegmentSource.User || !db.DisabledItems.Any(d => d.ItemId == itemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared query shape of the item-segment reads: the item's rows, tombstones
    /// excluded unless requested, ordered by mode and start time.
    /// </summary>
    private static IOrderedQueryable<DbSegment> OrderedItemSegments(IntroSkipperDbContext db, Guid itemId, bool includeSuppressed) =>
        db.Segments
            .AsNoTracking()
            .Where(s => s.ItemId == itemId && (includeSuppressed || s.State == SegmentState.Active))
            .OrderBy(s => s.Type)
            .ThenBy(s => s.StartTicks);

    /// <inheritdoc/>
    public async Task DeleteItemSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.Segments
            .Where(s => s.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        await db.Segments
            .Where(s => s.Type == mode)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteSegmentsForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        // EF.Parameter binds the ID set as a single JSON parameter (json_each), so the
        // delete is one statement regardless of the item count.
        return await db.Segments
            .Where(s => EF.Parameter(ids).Contains(s.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateRange(long startTicks, long endTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endTicks, startTicks);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}
