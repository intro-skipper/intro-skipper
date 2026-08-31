// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Segment (<see cref="DbSegment"/>) operations of <see cref="IntroSkipperDatabase"/>.
/// The user-mutation semantics live in <c>*CoreAsync</c> methods that operate on a
/// caller-owned context, so a caller composing several mutations (or a mutation plus
/// bookkeeping) into one transaction reuses exactly the same implementation as the
/// public single-shot methods.
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

                // Journal the item's projection with the rows in one transaction when
                // the servable image changed (a row added or removed; kept rows only
                // rewrite bookkeeping the mirror does not carry), so an analysis
                // write can never lose its mirror push to a crash. The projection
                // worker's poll converges the item.
                if (accepted.Count > 0 || autoRows.Count > kept)
                {
                    await EnqueueProjectionAsync(db, itemId, cancellationToken).ConfigureAwait(false);
                }

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

    /// <summary>
    /// Single-shot form of <see cref="AddUserSegmentCoreAsync"/>: adds a user segment,
    /// resolving an exact-range collision in place (an automatic row is promoted, a
    /// suppressed row revived, an existing user row returned unchanged; a promoted row
    /// loses its <see cref="DbSegment.ConfigHash"/>). Internal on purpose — it does not
    /// journal a projection, so production writes go through
    /// <see cref="ApplyChangeAsync"/>; this is the domain-semantics test seam over the
    /// same core.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="startTicks">Start time in ticks.</param>
    /// <param name="endTicks">End time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored row.</returns>
    internal async Task<DbSegment> AddUserSegmentAsync(
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
        return await AddUserSegmentCoreAsync(db, itemId, mode, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core of <see cref="AddUserSegmentAsync"/> on a caller-owned context. Saves its own
    /// changes — the concurrency recovery needs the save boundaries. Inside a caller's
    /// transaction a failed save rolls back to EF's automatic savepoint, not the whole
    /// transaction, so the recovery paths behave exactly as they do stand-alone.
    /// </summary>
    private static async Task<DbSegment> AddUserSegmentCoreAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        AnalysisMode mode,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Single-shot form of <see cref="ReplaceUserSegmentsCoreAsync"/>: per mode,
    /// deletes every active segment and stores the given user segments in one
    /// transaction (exact-range rows survive in place, keeping the ids Jellyfin
    /// knows). Internal on purpose — it does not journal a projection, so production
    /// writes go through <see cref="ApplyChangeAsync"/>; this is the domain-semantics
    /// test seam over the same core.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="segmentsByMode">The user segment to store per mode, in ticks; each end must be after its start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal async Task ReplaceUserSegmentsAsync(
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

        var byMode = segmentsByMode.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<(long StartTicks, long EndTicks)>)new[] { pair.Value });

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await ReplaceUserSegmentsCoreAsync(db, itemId, byMode, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Core of <see cref="ReplaceUserSegmentsAsync"/> on a caller-owned context and
    /// transaction, generalized to a complete range set per mode. For each mode, a row
    /// occupying exactly a requested range survives in place — keeping the id Jellyfin
    /// knows: an active row is promoted, a tombstone revived; either way it cannot
    /// collide with itself on the unique index. The mode's other active rows are
    /// removed and requested ranges without an occupant are inserted as user rows.
    /// Stages the changes without saving; the caller saves and commits.
    /// </summary>
    /// <returns>The surviving user rows, in request order per mode.</returns>
    private static async Task<IReadOnlyList<DbSegment>> ReplaceUserSegmentsCoreAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        IReadOnlyDictionary<AnalysisMode, IReadOnlyList<(long StartTicks, long EndTicks)>> segmentsByMode,
        CancellationToken cancellationToken)
    {
        var modes = segmentsByMode.Keys.ToArray();
        var existing = await db.Segments
            .Where(s => s.ItemId == itemId && modes.Contains(s.Type))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var survivors = new List<DbSegment>();
        foreach (var (mode, ranges) in segmentsByMode)
        {
            var kept = new List<DbSegment>();
            foreach (var (startTicks, endTicks) in ranges.Distinct())
            {
                var row = existing.Find(s => s.Type == mode && s.StartTicks == startTicks && s.EndTicks == endTicks);
                if (row is not null)
                {
                    row.PromoteToUser();
                }
                else
                {
                    row = new DbSegment(itemId, mode, startTicks, endTicks, SegmentSource.User);
                    db.Segments.Add(row);
                }

                kept.Add(row);
            }

            db.Segments.RemoveRange(existing.Where(s => s.Type == mode && s.State == SegmentState.Active && !kept.Contains(s)));
            survivors.AddRange(kept);
        }

        return survivors;
    }

    /// <summary>
    /// Single-shot form of <see cref="UpdateSegmentCoreAsync"/>: moves a segment's
    /// boundaries and promotes the surviving row to user provenance (an exact-range
    /// occupant of the same mode absorbs the addressed row and survives). Returns
    /// <see langword="null"/> when the id is unknown on the item, suppressed, or
    /// vanished concurrently. Internal on purpose — it does not journal a projection,
    /// so production writes go through <see cref="ApplyChangeAsync"/>; this is the
    /// domain-semantics test seam over the same core.
    /// </summary>
    /// <param name="itemId">Item ID that must own the segment; ids on other items are treated as unknown.</param>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="startTicks">New start time in ticks.</param>
    /// <param name="endTicks">New end time in ticks; must be after <paramref name="startTicks"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The surviving row, or <see langword="null"/>.</returns>
    internal async Task<DbSegment?> UpdateSegmentAsync(
        Guid itemId,
        Guid segmentId,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(startTicks, endTicks);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await UpdateSegmentCoreAsync(db, itemId, segmentId, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core of <see cref="UpdateSegmentAsync"/> on a caller-owned context. Saves its own
    /// changes — the concurrency recovery needs the save boundaries (see
    /// <see cref="AddUserSegmentCoreAsync"/> for the savepoint behavior inside a caller's
    /// transaction).
    /// </summary>
    private static async Task<DbSegment?> UpdateSegmentCoreAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        Guid segmentId,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Single-shot form of <see cref="DeleteSegmentCoreAsync"/>: tombstones automatic
    /// rows, hard-deletes user rows; returns a pre-delete snapshot, or
    /// <see langword="null"/> when the id is unknown on the item or already
    /// suppressed. Internal on purpose — it does not journal a projection, so
    /// production writes go through <see cref="ApplyChangeAsync"/>; this is the
    /// domain-semantics test seam over the same core.
    /// </summary>
    /// <param name="itemId">Item ID that must own the segment; ids on other items are treated as unknown.</param>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A pre-delete snapshot of the removed row, or <see langword="null"/>.</returns>
    internal async Task<DbSegment?> DeleteSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var snapshot = await DeleteSegmentCoreAsync(db, itemId, segmentId, cancellationToken).ConfigureAwait(false);
        if (snapshot is not null)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <summary>
    /// Core of <see cref="DeleteSegmentAsync"/> on a caller-owned context. Stages the
    /// delete (tombstone or removal) without saving; the caller saves and commits.
    /// </summary>
    private static async Task<DbSegment?> DeleteSegmentCoreAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
        var row = await db.Segments
            .FirstOrDefaultAsync(s => s.ItemId == itemId && s.Id == segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.State == SegmentState.Suppressed)
        {
            return null;
        }

        return StageDelete(db, row);
    }

    /// <summary>
    /// Stages the delete of one active tracked row — the single home of the delete
    /// rule: automatic rows tombstone (so re-analysis cannot resurrect the range),
    /// user rows go for good. Returns a pre-delete snapshot; nothing is saved.
    /// </summary>
    private static DbSegment StageDelete(IntroSkipperDbContext db, DbSegment row)
    {
        var snapshot = row.Clone();
        if (row.Source == SegmentSource.User)
        {
            db.Segments.Remove(row);
        }
        else
        {
            row.State = SegmentState.Suppressed;
        }

        return snapshot;
    }

    /// <summary>
    /// Single-shot form of <see cref="RestoreSegmentCoreAsync"/>: clears a tombstone,
    /// re-arming the analysis record and dropping the row's
    /// <see cref="DbSegment.ConfigHash"/>; returns <see langword="null"/> when the id
    /// is unknown on the item or not suppressed. Internal on purpose — it does not
    /// journal a projection, so production writes go through
    /// <see cref="ApplyChangeAsync"/>; this is the domain-semantics test seam over
    /// the same core.
    /// </summary>
    /// <param name="itemId">Item ID that must own the segment; ids on other items are treated as unknown.</param>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The restored row, or <see langword="null"/>.</returns>
    internal async Task<DbSegment?> RestoreSegmentAsync(Guid itemId, Guid segmentId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        return await RestoreSegmentCoreAsync(db, itemId, segmentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core of <see cref="RestoreSegmentAsync"/> on a caller-owned context. Saves its own
    /// changes — the concurrency recovery needs the save boundary (see
    /// <see cref="AddUserSegmentCoreAsync"/> for the savepoint behavior inside a caller's
    /// transaction).
    /// </summary>
    private static async Task<DbSegment?> RestoreSegmentCoreAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        Guid segmentId,
        CancellationToken cancellationToken)
    {
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
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // A concurrent analysis wrote the (ItemId, Type) record between the
            // set-if-absent check and this save; that record is newer state and wins.
            // Detach the staged re-arm and persist the restore alone.
            foreach (var entry in db.ChangeTracker.Entries<DbAnalyzedItem>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return row;
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

            // Journal every erased item's projection with the erase, so Jellyfin's
            // rows converge away even if the process dies before the mirror is pushed.
            await EnqueueProjectionsAsync(db, itemIds, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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
