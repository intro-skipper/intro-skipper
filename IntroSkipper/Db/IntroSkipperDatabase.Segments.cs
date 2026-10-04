// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Segment (<see cref="DbSegment"/>) operations of <see cref="IntroSkipperDatabase"/>.
/// The user-mutation semantics live in <c>*CoreAsync</c> methods that operate on a
/// caller-owned context; <see cref="ApplyChangeAsync"/> composes them with the
/// analysis bookkeeping and the projection journal in one transaction.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
    /// <summary>
    /// Atomically replaces the active automatic segments the writing pass produced for
    /// an item and mode with the admitted subset of <paramref name="segments"/>
    /// (<see cref="AutoSegmentAdmissionPolicy"/>: tombstones, user rows and intro
    /// overlap for credits reject a candidate; exact matches of the other pass or of
    /// an earlier candidate are dropped). Rows whose boundaries match an accepted
    /// segment keep their ids; an empty list clears the pass's rows; a non-empty list
    /// whose candidates were all rejected leaves the standing rows untouched. User
    /// segments and tombstones are never touched. A write that changes the servable
    /// image journals the item's projection in the same transaction.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode the segments belong to.</param>
    /// <param name="segments">Detected segments in seconds.</param>
    /// <param name="source">Analyzer that produced the segments; must not be <see cref="SegmentSource.User"/>.</param>
    /// <param name="configHash">Configuration hash that produced the segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of the pass's active automatic segments written or kept;
    /// 0 for a fully rejected write.</returns>
    public Task<int> ReplaceAutoSegmentsAsync(
        Guid itemId,
        AnalysisMode mode,
        IReadOnlyList<Segment> segments,
        SegmentSource source,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        if (source == SegmentSource.User)
        {
            throw new ArgumentException("Analysis writes must not use the User source.", nameof(source));
        }

        return ReplaceAutoSegmentsCoreAsync(
            itemId,
            mode,
            [.. segments.Select(s => new AttributedSegment(s, source))],
            derivedWrite: source == SegmentSource.CreditsDerived,
            configHash,
            cancellationToken);
    }

    /// <summary>
    /// The per-segment-source form of <see cref="ReplaceAutoSegmentsAsync(Guid, AnalysisMode, IReadOnlyList{Segment}, SegmentSource, string, CancellationToken)"/>
    /// for a pass whose segments come from different analyzers. Same admission,
    /// id-keeping and journaling rules; the write is attributed to the mode's own pass,
    /// never to the credits-derived preview pass.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode the segments belong to.</param>
    /// <param name="segments">Detected segments in seconds, each with its source; no source may be <see cref="SegmentSource.User"/> or <see cref="SegmentSource.CreditsDerived"/>.</param>
    /// <param name="configHash">Configuration hash that produced the segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of the pass's active automatic segments written or kept;
    /// 0 for a fully rejected write.</returns>
    public Task<int> ReplaceAutoSegmentsAsync(
        Guid itemId,
        AnalysisMode mode,
        IReadOnlyList<AttributedSegment> segments,
        string configHash = "",
        CancellationToken cancellationToken = default)
    {
        if (segments.Any(s => s.Source is SegmentSource.User or SegmentSource.CreditsDerived))
        {
            throw new ArgumentException("Per-segment analysis writes must not use the User or CreditsDerived source.", nameof(segments));
        }

        return ReplaceAutoSegmentsCoreAsync(itemId, mode, segments, derivedWrite: false, configHash, cancellationToken);
    }

    private async Task<int> ReplaceAutoSegmentsCoreAsync(
        Guid itemId,
        AnalysisMode mode,
        IReadOnlyList<AttributedSegment> segments,
        bool derivedWrite,
        string configHash,
        CancellationToken cancellationToken)
    {
        ValidateMode(mode);

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // One read for the mode's rows plus, for a credits write, the intros the
            // admission policy compares against.
            var loadIntros = mode == AnalysisMode.Credits;
            var itemRows = await db.Segments
                .Where(s => s.ItemId == itemId && (s.Type == mode || (loadIntros && s.Type == AnalysisMode.Introduction)))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var existing = itemRows.Where(s => s.Type == mode).ToList();

            // Credits-derived previews belong to the credits pass and every other
            // automatic row to its own mode's pass (the attribution rule of
            // CleanStaleAutomaticSegmentsAsync), so a write replaces only the rows
            // its own pass produced. Without the split, the Preview pass and the
            // credits derive would each delete the other's preview row.
            var activeAutoRows = existing.Where(s => s.State == SegmentState.Active && s.Source != SegmentSource.User).ToList();
            var autoRows = activeAutoRows.Where(s => (s.Source == SegmentSource.CreditsDerived) == derivedWrite).ToList();
            var otherPassRows = activeAutoRows.Where(s => (s.Source == SegmentSource.CreditsDerived) != derivedWrite).ToList();

            var accepted = new List<DbSegment>();
            var rejected = 0;
            foreach (var (segment, source) in segments.OrderBy(s => s.Segment.Start))
            {
                if (!TickConversions.TryFromSecondsRange(segment.Start, segment.End, out var startTicks, out var endTicks))
                {
                    rejected++;
                    continue;
                }

                var rejection = AutoSegmentAdmissionPolicy.Check(mode, startTicks, endTicks, itemRows);
                if (rejection != AutoSegmentRejection.None)
                {
                    switch (rejection)
                    {
                        case AutoSegmentRejection.Tombstone:
                            LogAutoSegmentSuppressedByTombstone(_logger, mode, itemId);
                            break;
                        case AutoSegmentRejection.UserSegment:
                            LogAutoSegmentSkippedForUserOverlap(_logger, mode, itemId);
                            break;
                        case AutoSegmentRejection.Introduction:
                            LogCreditsOverlapWithIntro(_logger, itemId);
                            break;
                    }

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
                    row.Source = match.Source;
                    row.ConfigHash = configHash;
                    kept++;
                }
                else
                {
                    db.Segments.Remove(row);
                }
            }

            db.Segments.AddRange(accepted);

            // Journal only when the servable image changed: kept rows rewrite
            // bookkeeping the mirror does not carry.
            if (accepted.Count > 0 || autoRows.Count > kept)
            {
                await EnqueueProjectionAsync(db, itemId, cancellationToken).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return kept + accepted.Count;
        }
    }

    /// <summary>
    /// The tracked row occupying exactly the range on the item and mode, or
    /// <see langword="null"/>; the read every exact-range collision rule starts from.
    /// </summary>
    private static Task<DbSegment?> FindExactRangeAsync(IntroSkipperDbContext db, Guid itemId, AnalysisMode mode, long startTicks, long endTicks, CancellationToken cancellationToken)
        => db.Segments.FirstOrDefaultAsync(
            s => s.ItemId == itemId && s.Type == mode && s.StartTicks == startTicks && s.EndTicks == endTicks,
            cancellationToken);

    /// <summary>
    /// The tracked row with the id on the item, or <see langword="null"/>; ids on other
    /// items are unknown by contract.
    /// </summary>
    private static Task<DbSegment?> FindOwnedRowAsync(IntroSkipperDbContext db, Guid itemId, Guid segmentId, CancellationToken cancellationToken)
        => db.Segments.FirstOrDefaultAsync(s => s.ItemId == itemId && s.Id == segmentId, cancellationToken);

    /// <summary>
    /// Adds a user segment on a caller-owned context, given the caller's read of the
    /// exact-range occupant (<paramref name="exact"/>, tracked): an automatic row is
    /// promoted, a suppressed row revived, an existing user row returned unchanged; a
    /// promoted row loses its <see cref="DbSegment.ConfigHash"/>.
    /// Saves its own changes because the concurrency recovery needs the save boundaries.
    /// Inside a caller's transaction a failed save rolls back to EF's automatic
    /// savepoint, not the whole transaction, so the recovery paths behave exactly as
    /// they do stand-alone.
    /// </summary>
    private static async Task<DbSegment> AddUserSegmentCoreAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        AnalysisMode mode,
        long startTicks,
        long endTicks,
        DbSegment? exact,
        CancellationToken cancellationToken)
    {
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
            var occupant = await FindExactRangeAsync(db, itemId, mode, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
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
    /// Replaces one mode's active segments with the given user ranges on a
    /// caller-owned context and transaction. A row occupying exactly a requested
    /// range survives in place, keeping the id Jellyfin knows: an active row is
    /// promoted, a tombstone revived; either way it cannot collide with itself on the
    /// unique index. The mode's other active rows are removed and requested ranges
    /// without an occupant are inserted as user rows. Stages the changes without
    /// saving; the caller saves and commits.
    /// </summary>
    /// <returns>The surviving user rows, in request order.</returns>
    private static async Task<IReadOnlyList<DbSegment>> ReplaceUserSegmentsCoreAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        AnalysisMode mode,
        IReadOnlyList<(long StartTicks, long EndTicks)> ranges,
        CancellationToken cancellationToken)
    {
        var existing = await db.Segments
            .Where(s => s.ItemId == itemId && s.Type == mode)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var kept = new List<DbSegment>();
        foreach (var (startTicks, endTicks) in ranges.Distinct())
        {
            var row = existing.Find(s => s.StartTicks == startTicks && s.EndTicks == endTicks);
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

        db.Segments.RemoveRange(existing.Where(s => s.State == SegmentState.Active && !kept.Contains(s)));
        return kept;
    }

    /// <summary>
    /// Replaces every active segment of an item in one tracked transaction. A requested
    /// row is matched by exact mode and range first, then by its supplied id, so a
    /// complete editor round-trip keeps Jellyfin's stable ids without colliding with
    /// an existing exact range. Rows omitted from the
    /// image use the normal delete rule: user rows are removed and automatic rows are
    /// tombstoned. Suppressed rows that remain omitted are left in place as tombstones.
    /// </summary>
    /// <returns>The active user rows in request order.</returns>
    private static async Task<IReadOnlyList<DbSegment>> ReplaceUserSegmentsForItemCoreAsync(
        IntroSkipperDbContext db,
        Guid itemId,
        IReadOnlyList<UserSegmentInput> requested,
        CancellationToken cancellationToken)
    {
        var existing = await db.Segments
            .Where(s => s.ItemId == itemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var kept = new List<DbSegment>(requested.Count);
        foreach (var input in requested)
        {
            // Prefer an exact range occupant: it avoids a unique-index collision
            // when a caller sends a stale id while another editor already owns the
            // requested range, and still preserves a stable id wherever possible.
            var row = existing.Find(s => s.Type == input.Mode
                && s.StartTicks == input.StartTicks
                && s.EndTicks == input.EndTicks);

            row ??= input.Id is { } id && id != Guid.Empty
                ? existing.Find(s => s.Id == id && s.Type == input.Mode && s.State != SegmentState.Suppressed)
                : null;

            if (row is null)
            {
                row = new DbSegment(itemId, input.Mode, input.StartTicks, input.EndTicks, SegmentSource.User);
                db.Segments.Add(row);
            }
            else
            {
                row.StartTicks = input.StartTicks;
                row.EndTicks = input.EndTicks;
                row.PromoteToUser();
            }

            kept.Add(row);
        }

        // This is intentionally staged before SaveChanges, but remains in the same
        // database transaction as every promotion/insertion above. There is no window
        // in which a partial desired image can be observed or journaled.
        foreach (var row in existing.Where(s => s.State == SegmentState.Active && !kept.Contains(s)))
        {
            StageDelete(db, row);
        }

        return kept;
    }

    /// <summary>
    /// Moves a segment's boundaries and promotes the surviving row to user provenance
    /// on a caller-owned context, given the caller's read of the active tracked
    /// <paramref name="row"/>: an exact-range occupant of the same mode absorbs the
    /// addressed row and survives. Returns <see langword="null"/> when the row vanished
    /// concurrently. Saves its own
    /// changes — the concurrency recovery needs the save boundaries (see
    /// <see cref="AddUserSegmentCoreAsync"/> for the savepoint behavior inside a caller's
    /// transaction).
    /// </summary>
    private static async Task<DbSegment?> UpdateSegmentCoreAsync(
        IntroSkipperDbContext db,
        DbSegment row,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken)
    {
        var itemId = row.ItemId;
        var segmentId = row.Id;
        var occupant = await FindOccupantAsync(db, row, startTicks, endTicks, cancellationToken).ConfigureAwait(false);

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
                    // the add's in-place promotion, the occupant (whose id
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
            var lateOccupant = await FindOccupantAsync(db, row, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
            if (lateOccupant is null)
            {
                throw;
            }

            var movedRow = await FindOwnedRowAsync(db, itemId, segmentId, cancellationToken).ConfigureAwait(false);
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

    // Another row of the same item and mode sitting exactly on the target range.
    private static Task<DbSegment?> FindOccupantAsync(IntroSkipperDbContext db, DbSegment row, long startTicks, long endTicks, CancellationToken cancellationToken)
    {
        var itemId = row.ItemId;
        var segmentId = row.Id;
        var mode = row.Type;
        return db.Segments.FirstOrDefaultAsync(
            s => s.Id != segmentId
                && s.ItemId == itemId
                && s.Type == mode
                && s.StartTicks == startTicks
                && s.EndTicks == endTicks,
            cancellationToken);
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
    /// Clears a tombstone on a caller-owned context, given the caller's read of the
    /// suppressed tracked <paramref name="row"/>: re-arms the analysis record and drops
    /// the row's <see cref="DbSegment.ConfigHash"/>. Saves its own
    /// changes — the concurrency recovery needs the save boundary (see
    /// <see cref="AddUserSegmentCoreAsync"/> for the savepoint behavior inside a caller's
    /// transaction).
    /// </summary>
    private static async Task<DbSegment> RestoreSegmentCoreAsync(
        IntroSkipperDbContext db,
        DbSegment row,
        CancellationToken cancellationToken)
    {
        var itemId = row.ItemId;
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

    /// <summary>
    /// Returns the stored segments of an item, ordered by mode and start time.
    /// Tombstones are excluded unless <paramref name="includeSuppressed"/> is set.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="includeSuppressed">Whether to include suppressed rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored segments.</returns>
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

    /// <summary>
    /// Returns the item's active segments as served to clients: automatic rows are
    /// withheld while the item is disabled, user-provided rows always pass. Every
    /// client-facing surface (the Jellyfin mirror and the provider) reads through
    /// this; editor and analysis reads use <see cref="GetSegmentsAsync"/>.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The servable segments, ordered by mode and start time.</returns>
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

    /// <summary>
    /// Deletes every stored segment of the given analysis mode, tombstones included
    /// (explicit erase is a factory reset), and the mode's analysis records so the next
    /// scan re-detects instead of classifying the erased items as <c>NoSegments</c>.
    /// Items whose erased rows were credits-derived also lose their
    /// <see cref="AnalysisMode.Credits"/> records, because only the credits pass can
    /// regenerate those rows. Everything runs in one transaction, the affected items'
    /// projections journaled with it, so their Jellyfin mirrors converge durably.
    /// </summary>
    /// <param name="mode">Analysis mode to erase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ids of the items that held a segment of the mode.</returns>
    public async Task<IReadOnlyCollection<Guid>> DeleteSegmentsByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // Credits-derived rows are produced by the credits pass, so erasing them must
            // also reopen that pass for their items — the erased mode's own pass cannot
            // regenerate them and would just settle the items as NoSegments.
            var derivedItemIds = await db.Segments
                .Where(s => s.Type == mode && s.Source == SegmentSource.CreditsDerived)
                .Select(s => s.ItemId)
                .Distinct()
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            var (_, itemIds) = await DeleteSegmentsAndJournalAsync(
                db,
                db.Segments.Where(s => s.Type == mode),
                cancellationToken).ConfigureAwait(false);

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

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return itemIds;
        }
    }

    /// <summary>
    /// Removes active Preview segments derived from Credits for the supplied items, preserving
    /// user segments and previews produced by the regular Preview pass.
    /// </summary>
    /// <param name="itemIds">Item IDs to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of credits-derived preview rows removed.</returns>
    public async Task<int> ClearCreditsDerivedPreviewsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        Guid[] ids = [.. itemIds.Distinct()];
        if (ids.Length == 0)
        {
            return 0;
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var doomedRows = db.Segments.Where(s =>
                EF.Parameter(ids).Contains(s.ItemId)
                && s.Type == AnalysisMode.Preview
                && s.Source == SegmentSource.CreditsDerived
                && s.State == SegmentState.Active);
            var (removed, _) = await DeleteSegmentsAndJournalAsync(db, doomedRows, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return removed;
        }
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
