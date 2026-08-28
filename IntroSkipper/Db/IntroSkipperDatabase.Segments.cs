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
        ValidateMode(mode);
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

                // Credits-derived previews belong to the credits pass and every other
                // automatic row to its own mode's pass (the attribution rule of
                // CleanStaleAutomaticSegmentsAsync), so a write replaces only the rows
                // its own pass produced. Without the split, the Preview pass and the
                // credits derive would each delete the other's preview row.
                var derivedWrite = source == SegmentSource.CreditsDerived;
                var activeAutoRows = existing.Where(s => s.State == SegmentState.Active && s.Source != SegmentSource.User).ToList();
                var autoRows = activeAutoRows.Where(s => (s.Source == SegmentSource.CreditsDerived) == derivedWrite).ToList();
                var otherPassRows = activeAutoRows.Where(s => (s.Source == SegmentSource.CreditsDerived) != derivedWrite).ToList();

                var intros = mode == AnalysisMode.Credits
                    ? await db.Segments
                        .AsNoTracking()
                        .Where(s => s.ItemId == itemId && s.Type == AnalysisMode.Introduction && s.State == SegmentState.Active)
                        .Select(s => new { s.StartTicks, s.EndTicks })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false)
                    : [];

                var accepted = new List<DbSegment>();
                var rejected = 0;
                foreach (var segment in segments.OrderBy(s => s.Start))
                {
                    if (!TickConversions.TryFromSecondsRange(segment.Start, segment.End, out var startTicks, out var endTicks))
                    {
                        rejected++;
                        continue;
                    }

                    if (tombstones.Any(t => AutoSegmentAdmissionPolicy.Overlaps(startTicks, endTicks, t.StartTicks, t.EndTicks)))
                    {
                        LogAutoSegmentSuppressedByTombstone(_logger, mode, itemId);
                        rejected++;
                        continue;
                    }

                    if (userRows.Any(u => AutoSegmentAdmissionPolicy.Overlaps(startTicks, endTicks, u.StartTicks, u.EndTicks)))
                    {
                        LogAutoSegmentSkippedForUserOverlap(_logger, mode, itemId);
                        rejected++;
                        continue;
                    }

                    if (mode == AnalysisMode.Credits
                        && intros.Any(i => AutoSegmentAdmissionPolicy.Overlaps(startTicks, endTicks, i.StartTicks, i.EndTicks)))
                    {
                        LogCreditsOverlapWithIntro(_logger, itemId);
                        rejected++;
                        continue;
                    }

                    // An identical range already standing under the other pass keeps that
                    // pass's row; re-inserting it would violate the unique
                    // (ItemId, Type, StartTicks, EndTicks) index.
                    if (otherPassRows.Any(o => o.StartTicks == startTicks && o.EndTicks == endTicks))
                    {
                        continue;
                    }

                    if (accepted.Any(a => a.StartTicks == startTicks && a.EndTicks == endTicks))
                    {
                        continue;
                    }

                    accepted.Add(new DbSegment(itemId, mode, startTicks, endTicks, source, configHash));
                }

                // A write whose candidates were all rejected by the admission gate must not
                // clear the pass's standing rows: each rejection records human intent
                // (tombstone, user row) or policy (credits vs intro), not evidence that the
                // standing detection went stale - stale rows are
                // CleanStaleAutomaticSegmentsAsync's job. Candidates satisfied by an exact
                // other-pass row are not rejections, so the normal replace still runs for
                // them, and an empty input list still clears the pass's rows as documented.
                if (accepted.Count == 0 && rejected > 0)
                {
                    return 0;
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
        ValidateMode(mode);
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
            exact.PromoteToUser();
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return exact;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent analysis write deleted the row between the read and the
                // promotion; fall through and insert the user segment fresh.
                db.ChangeTracker.Clear();
            }
        }

        var row = new DbSegment(itemId, mode, startTicks, endTicks, SegmentSource.User);
        db.Segments.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return row;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // A concurrent analysis write claimed the exact range between the read and
            // this insert (analyzers do not take the editor's stripe); resolve like the
            // up-front exact match — promote the occupant in place.
            db.ChangeTracker.Clear();
            var occupant = await db.Segments
                .FirstOrDefaultAsync(
                    s => s.ItemId == itemId && s.Type == mode && s.StartTicks == startTicks && s.EndTicks == endTicks,
                    cancellationToken)
                .ConfigureAwait(false);
            if (occupant is null)
            {
                throw;
            }

            occupant.PromoteToUser();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return occupant;
        }
    }

    /// <inheritdoc/>
    public async Task ReplaceUserSegmentsAsync(
        Guid itemId,
        IReadOnlyDictionary<AnalysisMode, (long StartTicks, long EndTicks)> segmentsByMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segmentsByMode);
        foreach (var (mode, (startTicks, endTicks)) in segmentsByMode)
        {
            ValidateMode(mode);
            ValidateRange(startTicks, endTicks);
        }

        if (segmentsByMode.Count == 0)
        {
            return;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var modes = segmentsByMode.Keys.ToArray();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var existing = await db.Segments
                .Where(s => s.ItemId == itemId && modes.Contains(s.Type))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var (mode, (startTicks, endTicks)) in segmentsByMode)
            {
                // A row with exactly the new range survives in place (keeping the id
                // Jellyfin knows): an active row is promoted, a tombstone revived; either
                // way it cannot collide with itself on the unique index. Only the mode's
                // other active rows go.
                var row = existing.Find(s => s.Type == mode && s.StartTicks == startTicks && s.EndTicks == endTicks);
                db.Segments.RemoveRange(existing.Where(s => s.Type == mode && s.State == SegmentState.Active && s != row));

                if (row is not null)
                {
                    row.PromoteToUser();
                }
                else
                {
                    db.Segments.Add(new DbSegment(itemId, mode, startTicks, endTicks, SegmentSource.User));
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        // Concurrent analysis writes do not take the editor's stripe, so the row (or the
        // occupant) can vanish between the reads above and the save. That surfaces as a
        // zero-row update; report the segment as unknown instead of failing the request.
        try
        {
            if (occupant is not null)
            {
                if (occupant.State != SegmentState.Suppressed)
                {
                    // The user explicitly claims an exactly-occupied active range: like
                    // AddUserSegmentAsync's in-place promotion, the occupant (whose id
                    // Jellyfin already knows) survives as the user segment and the moved
                    // row is absorbed into it.
                    db.Segments.Remove(row);
                    occupant.PromoteToUser();
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
            row.PromoteToUser();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return row;
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // A concurrent analysis write claimed the exact target range after the
            // occupant read above. Resolve like the up-front occupant path: the new
            // occupant (whose id Jellyfin already knows) survives as the user segment
            // and the moved row is absorbed into it.
            db.ChangeTracker.Clear();
            var lateOccupant = await db.Segments
                .FirstOrDefaultAsync(
                    s => s.Id != segmentId
                        && s.ItemId == itemId
                        && s.Type == row.Type
                        && s.StartTicks == startTicks
                        && s.EndTicks == endTicks,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lateOccupant is null)
            {
                throw;
            }

            var movedRow = await db.Segments
                .FirstOrDefaultAsync(s => s.ItemId == itemId && s.Id == segmentId, cancellationToken)
                .ConfigureAwait(false);
            if (movedRow is not null)
            {
                db.Segments.Remove(movedRow);
            }

            lateOccupant.PromoteToUser();
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return lateOccupant;
            }
            catch (DbUpdateConcurrencyException)
            {
                return null;
            }
        }
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
            // A row with the same range appeared in the meantime. For an automatic
            // snapshot the restore's intent is met — but a user snapshot must not
            // silently degrade to the occupant's provenance (a concurrent analysis
            // write would leave the range automatic, and a later hash cleanup would
            // delete it), so hand the occupant to the user.
            if (deletedSnapshot.Source != SegmentSource.User)
            {
                return;
            }

            db.ChangeTracker.Clear();
            var occupant = await db.Segments
                .FirstOrDefaultAsync(
                    s => s.ItemId == deletedSnapshot.ItemId
                        && s.Type == deletedSnapshot.Type
                        && s.StartTicks == deletedSnapshot.StartTicks
                        && s.EndTicks == deletedSnapshot.EndTicks,
                    cancellationToken)
                .ConfigureAwait(false);
            if (occupant is not null && occupant.Source != SegmentSource.User)
            {
                occupant.PromoteToUser();
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
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

        // The delete that tombstoned the row also cleared the item's analysis record
        // (so re-analysis could look for other segments). Restoring is the undo of that
        // delete, so re-arm the record with the hash the row carried — otherwise the
        // very next scan re-analyzes the item and the pass's replace silently deletes
        // the row the user explicitly brought back whenever the analyzer no longer
        // emits its exact boundaries. Only when absent: a record written since the
        // delete (analysis ran in between) is newer state and wins.
        if (row.ConfigHash.Length > 0
            && !await db.AnalyzedItems
                .AnyAsync(a => a.ItemId == itemId && a.Type == row.Type, cancellationToken)
                .ConfigureAwait(false))
        {
            db.AnalyzedItems.Add(new DbAnalyzedItem(itemId, row.Type, row.ConfigHash));
        }

        // The restore is recorded human intent, but the row stays automatic by contract.
        // Drop the analyzer's hash so the hash-driven stale cleanup (which only judges
        // rows carrying a hash) cannot silently delete what the user explicitly brought
        // back.
        row.ConfigHash = string.Empty;
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
    public async Task<IReadOnlyCollection<Guid>> DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var itemIds = await db.Segments
                .Where(s => s.Type == mode)
                .Select(s => s.ItemId)
                .Distinct()
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            // Credits-derived rows are produced by the credits pass, so erasing them must
            // also reopen that pass for their items — the erased mode's own pass cannot
            // regenerate them and would just settle the items as NoSegments.
            var derivedItemIds = await db.Segments
                .Where(s => s.Type == mode && s.Source == SegmentSource.CreditsDerived)
                .Select(s => s.ItemId)
                .Distinct()
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            await db.Segments
                .Where(s => s.Type == mode)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            // Without this the erased items stay recorded as analyzed for the mode and
            // VerifyQueueAsync settles them as NoSegments, so nothing would re-detect them.
            await db.AnalyzedItems
                .Where(a => a.Type == mode)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            if (derivedItemIds.Length > 0)
            {
                await db.AnalyzedItems
                    .Where(a => a.Type == AnalysisMode.Credits && EF.Parameter(derivedItemIds).Contains(a.ItemId))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return itemIds;
        }
    }

    private static void ValidateRange(long startTicks, long endTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTicks);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endTicks, startTicks);
    }

    // Every stored row must carry a mappable mode: downstream conversions index
    // AnalysisHelpers.ModeToSegmentType with it, so a persisted unmapped mode would
    // poison every later mirror of the item. The segments POST edge rejects such modes
    // with 400; this guards the invariant at the write boundary itself.
    private static void ValidateMode(AnalysisMode mode)
    {
        if (!AnalysisHelpers.IsSupported(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Analysis mode has no media segment type mapping.");
        }
    }

    // The extended result codes cover the Id primary key and the (ItemId, Type,
    // StartTicks, EndTicks) unique index; the primary code SQLITE_CONSTRAINT would also
    // swallow NOT NULL and CHECK violations, which do not mean an equivalent row
    // already exists.
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.InnerException is SqliteException
        {
            SqliteExtendedErrorCode: SQLitePCL.raw.SQLITE_CONSTRAINT_PRIMARYKEY or SQLitePCL.raw.SQLITE_CONSTRAINT_UNIQUE
        };
}
