// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Intent-based change application of <see cref="IntroSkipperDatabase"/>: one
/// transaction that runs the mutation cores and journals the resulting projection
/// work (a per-item queue marker, plus durable
/// foreign-row deletes), so a committed change can never lose its projection to a
/// crash. The journal records work, not data — projection re-derives the item's image
/// from current truth when it runs.
/// </summary>
internal sealed partial class IntroSkipperDatabase
{
    /// <summary>
    /// Rows mirrored before the shared-id scheme were converted from seconds by
    /// truncation while the legacy import rounds, so the two can sit one tick apart;
    /// this tolerance absorbs that without reintroducing range-level epsilon matching
    /// elsewhere.
    /// </summary>
    internal const long UncorrelatedTickTolerance = 1;

    /// <inheritdoc/>
    public async Task<MutationResult> ApplyChangeAsync(SegmentChangeIntent intent, Func<Task<ExternalSegmentTarget?>>? resolveExternalTarget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (Validate(intent) is { } rejection)
        {
            return new MutationResult(rejection, []);
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await MutateAsync(db, intent, resolveExternalTarget, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is Rejected || !result.Reproject)
            {
                // Every rejection — and every no-reproject Ignore, whose target
                // exists in no state at all — is decided before a core persists a
                // mutation, so disposing the transaction unwinds nothing that
                // matters, and a 404-style probe pays no journal write.
                return result;
            }

            // Accepted and Ignored both journal: an Ignored intent re-asserts state
            // the plugin database already holds, and re-projecting that state is how
            // a diverged mirror (a ghost or missing Jellyfin row) heals when the
            // user retries — the legacy editor synced idempotent requests for the
            // same reason.
            await EnqueueProjectionAsync(db, intent.ItemId, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>
    /// Marks the item's projection as behind. New work supersedes any backoff the
    /// previous failure earned; the attempt counter and failure text stay — they
    /// describe the item's projection health until an apply succeeds. Enqueue never
    /// consults a clock (<see langword="null"/> due time means due immediately), so
    /// test time providers stay authoritative over retry timing.
    /// </summary>
    private static Task EnqueueProjectionAsync(IntroSkipperDbContext db, Guid itemId, CancellationToken cancellationToken)
        => EnqueueProjectionsAsync(db, [itemId], cancellationToken);

    /// <summary>
    /// The single home of the marker-supersession rule, shared by the intent path
    /// (one item) and the bulk analysis/maintenance writes: an existing marker bumps
    /// its version with the due time reset, a missing one inserts. Runs inside the
    /// caller's transaction; the projection worker's poll picks the markers up, so
    /// bulk writers never await Jellyfin.
    /// </summary>
    private static async Task EnqueueProjectionsAsync(IntroSkipperDbContext db, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        // Atomic multi-row upserts, never a tracked read-modify-write: the analyzer
        // and maintenance callers hold no projection stripe, so the worker's
        // version-guarded completion can delete a marker at any moment — a tracked
        // update would then throw DbUpdateConcurrencyException at save time and roll
        // the caller's whole transaction back (the analyzed segments with it). The
        // upsert either beats the completion (whose stale delete then misses) or
        // follows it (the id inserts fresh). Ids are bound as parameters, never
        // spliced into JSON, so the key text matches what EF stores.
        var statements = MultiRowSql.Statements(
            itemIds.Distinct(),
            id => $"({id}, 1, 0, NULL, NULL)",
            rows => $"""
                INSERT INTO "ProjectionQueue" ("ItemId", "Version", "AttemptCount", "NextAttemptAt", "Failure")
                VALUES {rows}
                ON CONFLICT("ItemId") DO UPDATE SET "Version" = "Version" + 1, "NextAttemptAt" = NULL
                """);
        foreach (var statement in statements)
        {
            await db.Database.ExecuteSqlAsync(statement, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The bulk delete-and-journal kernel: materializes the doomed rows' ids once,
    /// deletes exactly those rows, and journals exactly their items' projections —
    /// so a delete and its markers can never be computed from two different row
    /// sets. Runs inside the caller's transaction; the caller saves and commits.
    /// </summary>
    /// <param name="db">Open context whose transaction the delete joins.</param>
    /// <param name="doomedRows">Query selecting the rows to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of segment rows deleted and the distinct ids of the items they belonged to.</returns>
    private static async Task<(int RemovedRows, IReadOnlyCollection<Guid> ItemIds)> DeleteSegmentsAndJournalAsync(IntroSkipperDbContext db, IQueryable<DbSegment> doomedRows, CancellationToken cancellationToken)
    {
        var doomed = await doomedRows
            .Select(s => new { s.Id, s.ItemId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (doomed.Count == 0)
        {
            return (0, []);
        }

        var doomedIds = doomed.Select(d => d.Id).ToArray();
        var removed = await db.Segments
            .Where(s => EF.Parameter(doomedIds).Contains(s.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        var itemIds = doomed.Select(d => d.ItemId).Distinct().ToArray();
        await EnqueueProjectionsAsync(db, itemIds, cancellationToken).ConfigureAwait(false);
        return (removed, itemIds);
    }

    /// <summary>
    /// Shape validation; every rejection an intent can earn from its own values is
    /// produced here, before the transaction opens. The editor delete's external-target
    /// checks depend on whether a plugin row owns the id and therefore live in its
    /// <see cref="MutateAsync"/> dispatch, after the lazily resolved target arrives.
    /// </summary>
    private static Rejected? Validate(SegmentChangeIntent intent)
    {
        static bool ValidMode(AnalysisMode mode) => AnalysisHelpers.IsSupported(mode);

        if (intent.ItemId == Guid.Empty)
        {
            return new(SegmentChangeRejectedReason.EmptyItemId, "Item ID must not be empty.");
        }

        return intent switch
        {
            AddUserSegmentIntent value when !ValidMode(value.Mode) || !TickConversions.IsValidTickRange(value.StartTicks, value.EndTicks) => new(SegmentChangeRejectedReason.InvalidModeOrRange, "Invalid mode or tick range."),
            ReplaceUserSegmentsForModeIntent value when value.Segments is null || !ValidMode(value.Mode) || value.Segments.Any(range => !TickConversions.IsValidTickRange(range.StartTicks, range.EndTicks)) => new(SegmentChangeRejectedReason.InvalidModeOrRange, "Invalid mode or tick range."),
            UpdateSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            UpdateSegmentIntent value when !TickConversions.IsValidTickRange(value.StartTicks, value.EndTicks) => new(SegmentChangeRejectedReason.InvalidSegmentIdOrRange, "Invalid tick range."),
            DeleteSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            RestoreSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            EditorDeleteSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            EditorDeleteSegmentIntent value when AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType) is null => new(SegmentChangeRejectedReason.InvalidExternalIdOrType, "Invalid segment type."),
            SegmentVisibilityChangeIntent value when value.SeasonId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySeasonId, "Season ID must not be empty."),
            _ => null
        };
    }

    /// <summary>
    /// Dispatches one validated intent to the shared mutation cores. Prechecks decide
    /// every Rejected outcome and every no-reproject Ignore before any core persists a
    /// change, so the caller can abandon the transaction on those; an Ignore that
    /// journals may additionally have staged a foreign-row operation.
    /// </summary>
    private static async Task<MutationResult> MutateAsync(IntroSkipperDbContext db, SegmentChangeIntent intent, Func<Task<ExternalSegmentTarget?>>? resolveExternalTarget, CancellationToken cancellationToken)
    {
        switch (intent)
        {
            case AddUserSegmentIntent value:
                {
                    var exact = await FindExactRangeAsync(db, value.ItemId, value.Mode, value.StartTicks, value.EndTicks, cancellationToken).ConfigureAwait(false);
                    if (exact is { Source: SegmentSource.User, State: SegmentState.Active })
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.UserSegmentAlreadyExists, "The user segment already exists.", [ToValue(exact)]);
                    }

                    var row = await AddUserSegmentCoreAsync(db, value.ItemId, value.Mode, value.StartTicks, value.EndTicks, exact, cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, [ToValue(row)]);
                }

            case ReplaceUserSegmentsForModeIntent value:
                {
                    var requested = value.Segments.Select(range => (range.StartTicks, range.EndTicks)).Distinct().ToList();
                    if (requested.Count == 0)
                    {
                        // An empty set is the mode-wide delete, with delete semantics:
                        // automatic rows tombstone (so re-analysis cannot resurrect
                        // them), user rows go for good, and the mode's analysis record
                        // clears so the next scan may look for other segments — even
                        // when only the record is left to clear. Ignored only when
                        // neither exists.
                        var activeRows = await db.Segments
                            .Where(s => s.ItemId == value.ItemId && s.Type == value.Mode && s.State == SegmentState.Active)
                            .ToListAsync(cancellationToken)
                            .ConfigureAwait(false);
                        var hasAnalysisRecord = await db.AnalyzedItems
                            .AnyAsync(a => a.ItemId == value.ItemId && a.Type == value.Mode, cancellationToken)
                            .ConfigureAwait(false);
                        if (activeRows.Count == 0 && !hasAnalysisRecord)
                        {
                            return MutationResult.Ignore(SegmentChangeIgnoredReason.UserImageAlreadyExists, "The mode already has no active segments.");
                        }

                        var affected = new List<SegmentValue>();
                        foreach (var row in activeRows)
                        {
                            affected.Add(ToDeletedValue(StageDelete(db, row)));
                        }

                        await ClearItemAnalysisCoreAsync(db, value.ItemId, value.Mode, cancellationToken).ConfigureAwait(false);
                        return new MutationResult(null, affected);
                    }

                    var active = await db.Segments.AsNoTracking()
                        .Where(s => s.ItemId == value.ItemId && s.Type == value.Mode && s.State == SegmentState.Active)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (active.Count == requested.Count && active.All(row => row.Source == SegmentSource.User && requested.Contains((row.StartTicks, row.EndTicks))))
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.UserImageAlreadyExists, "The requested user image already exists.");
                    }

                    var survivors = await ReplaceUserSegmentsCoreAsync(db, value.ItemId, value.Mode, requested, cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, survivors.Select(ToValue).ToList());
                }

            case UpdateSegmentIntent value:
                {
                    var row = await FindOwnedRowAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                    if (row is null || row.State == SegmentState.Suppressed)
                    {
                        return MutationResult.Reject(SegmentChangeRejectedReason.SegmentMissingOrSuppressed, "Segment was not found on the item or is suppressed.");
                    }

                    if (row.StartTicks == value.StartTicks && row.EndTicks == value.EndTicks && row.Source == SegmentSource.User)
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentAlreadyHasValues, "The segment already has the requested values.", [ToValue(row)]);
                    }

                    // A null after the precheck means a concurrent non-stripe writer
                    // removed the row mid-flight; the core persisted nothing then.
                    var updated = await UpdateSegmentCoreAsync(db, row, value.StartTicks, value.EndTicks, cancellationToken).ConfigureAwait(false);
                    return updated is null
                        ? MutationResult.Reject(SegmentChangeRejectedReason.SegmentMissingOrSuppressed, "Segment was not found on the item or is suppressed.")
                        : new MutationResult(null, [ToValue(updated)]);
                }

            case DeleteSegmentIntent value:
                {
                    var row = await FindOwnedRowAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                    if (row is null || row.State == SegmentState.Suppressed)
                    {
                        // A suppressed row keeps the journaled re-projection (its
                        // ghost Jellyfin row may linger); an id that exists in no
                        // state has nothing to heal.
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "Segment was not found on the item or was already deleted.", reproject: row is not null);
                    }

                    var deleted = await DeleteOwnedRowAsync(db, row, cancellationToken).ConfigureAwait(false);

                    // Like the editor delete's correlated arm: journal the Jellyfin
                    // twin's targeted delete with the row's own shape, so a retry of
                    // this delete through any surface answers idempotently via the
                    // pending-op guard while the sync is still pending.
                    await JournalExternalDeleteAsync(db, value.ItemId, value.SegmentId, AnalysisHelpers.ModeToSegmentType[deleted.Mode], deleted.StartTicks, deleted.EndTicks, cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, [deleted]);
                }

            case RestoreSegmentIntent value:
                {
                    var row = await FindOwnedRowAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                    if (row is null || row.State != SegmentState.Suppressed)
                    {
                        // Same classification as the delete: an existing (active) row
                        // keeps the healing re-projection, a missing id journals nothing.
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrNotSuppressed, "Segment was not found on the item or was not suppressed.", reproject: row is not null);
                    }

                    var restored = await RestoreSegmentCoreAsync(db, row, cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, [ToValue(restored)]);
                }

            case EditorDeleteSegmentIntent value:
                {
                    // The delete dispatch, decided entirely inside the transaction so
                    // a concurrent mutation cannot invalidate the chosen path: a
                    // plugin row sharing the id is deleted authoritatively, and only
                    // an uncorrelated id resolves and validates the external row.
                    var mode = AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType)!.Value;
                    var itemRows = await db.Segments
                        .Where(s => s.ItemId == value.ItemId)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var correlated = itemRows.Find(s => s.Id == value.SegmentId);
                    if (correlated is not null)
                    {
                        if (correlated.Type != mode)
                        {
                            return MutationResult.Reject(
                                SegmentChangeRejectedReason.ExternalTypeMismatch,
                                TypeMismatchMessage(value.SegmentId, AnalysisHelpers.ModeToSegmentType[correlated.Type], value.ExpectedType));
                        }

                        // The correlated row's Jellyfin twin shares its id and shape,
                        // so its targeted delete is journaled with the row — for a
                        // suppressed row that heals a lingering ghost, and either way
                        // it durably records that this external row was addressed, so
                        // a retry whose sync is still pending answers idempotently
                        // instead of re-matching (see the pending-op guard below).
                        await JournalExternalDeleteAsync(db, value.ItemId, value.SegmentId, value.ExpectedType, correlated.StartTicks, correlated.EndTicks, cancellationToken).ConfigureAwait(false);
                        if (correlated.State == SegmentState.Suppressed)
                        {
                            // The plugin already treats the row as deleted; the delete
                            // is idempotently satisfied.
                            return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "The segment is already deleted.");
                        }

                        return new MutationResult(null, [await DeleteOwnedRowAsync(db, correlated, cancellationToken).ConfigureAwait(false)]);
                    }

                    // No plugin row owns the id: resolve the Jellyfin row now — only
                    // this arm needs the read, and the caller's stripe keeps it
                    // race-free against concurrent projections — and require it to
                    // corroborate the request before the external delete runs.
                    var target = resolveExternalTarget is null
                        ? null
                        : await resolveExternalTarget().ConfigureAwait(false);
                    if (target is null || target.Id != value.SegmentId)
                    {
                        // The row may be missing because this very delete's journaled
                        // operation already removed it while the item sync behind it
                        // is still pending (the projection deletes foreign rows before
                        // it syncs), so a retry must not 404. With nothing resolved
                        // the pending-op guard answers by id and requested type —
                        // there is no shape to compare, and no row for the mode-wide
                        // fallback to mis-claim, so the weaker match stays safe.
                        if (await HasJournaledExternalDeleteForIdAsync(db, value.ItemId, value.SegmentId, value.ExpectedType, cancellationToken).ConfigureAwait(false))
                        {
                            return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "The external segment's delete is already journaled.");
                        }

                        return MutationResult.Reject(SegmentChangeRejectedReason.ExternalSegmentNotFound, "External segment was not found.");
                    }

                    if (target.ItemId != value.ItemId)
                    {
                        return MutationResult.Reject(SegmentChangeRejectedReason.ExternalItemMismatch, "External segment belongs to another item.");
                    }

                    if (target.Type != value.ExpectedType)
                    {
                        return MutationResult.Reject(
                            SegmentChangeRejectedReason.ExternalTypeMismatch,
                            TypeMismatchMessage(value.SegmentId, target.Type, value.ExpectedType));
                    }

                    return await DeleteExternalRowAsync(db, value.ItemId, value.SegmentId, value.ExpectedType, target, itemRows, cancellationToken).ConfigureAwait(false);
                }

            case SegmentVisibilityChangeIntent value:
                {
                    var (_, changed) = await SetItemDisabledCoreAsync(db, value.SeasonId, value.ItemId, !value.Visible, cancellationToken).ConfigureAwait(false);
                    if (!changed)
                    {
                        return value.Visible
                            ? MutationResult.Ignore(SegmentChangeIgnoredReason.AlreadyVisible, "The item is already visible.")
                            : MutationResult.Ignore(SegmentChangeIgnoredReason.AlreadyHidden, "The item is already hidden.");
                    }

                    var visibleRows = await db.Segments.AsNoTracking()
                        .Where(s => s.ItemId == value.ItemId && s.State == SegmentState.Active && (value.Visible || s.Source == SegmentSource.User))
                        .OrderBy(s => s.Type).ThenBy(s => s.StartTicks)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return new MutationResult(null, visibleRows.Select(ToValue).ToList());
                }

            default:
                return MutationResult.Reject(SegmentChangeRejectedReason.UnsupportedIntent, "Unsupported segment change intent.");
        }
    }

    /// <summary>
    /// Deletes one exactly validated, uncorrelated Jellyfin row and its plugin
    /// counterpart. The counterpart is matched by the shared uncorrelated rule
    /// (1-tick tolerance, closest wins, non-commercial mode-wide fallback), without
    /// which a pre-shared-id row sitting one tick off would stay active and the very
    /// sync this change journals would resurrect the deleted segment. The fallback
    /// cannot re-claim after a concurrent or retried single-row delete: every such
    /// delete journals its target's operation, and the pending-op guard above
    /// answers those retries before matching runs. The journaled operation removes
    /// the foreign row either way, carrying the validated boundaries for the
    /// apply-time guard.
    /// </summary>
    private static async Task<MutationResult> DeleteExternalRowAsync(IntroSkipperDbContext db, Guid itemId, Guid externalSegmentId, MediaSegmentType expectedType, ExternalSegmentTarget target, List<DbSegment> itemRows, CancellationToken cancellationToken)
    {
        var mode = AnalysisHelpers.TryMapSegmentTypeToMode(expectedType)!.Value;

        // A pending journaled delete of this exact row shape means a concurrent or
        // retried request already recorded this delete, and its projection has not
        // applied yet (applies run under the same stripe the caller holds, so none
        // can be mid-flight). Matching again would be dangerous, not just redundant:
        // the earlier request's counterpart may be gone without a trace (a
        // hard-deleted user row), leaving the mode-wide fallback free to claim a
        // segment the caller never addressed. The shape comparison matters too: a
        // row rewritten under its stable id since the earlier request must fall
        // through and journal a fresh operation for the new shape — its old
        // operation drops harmlessly as superseded at apply time.
        if (await HasJournaledExternalDeleteAsync(db, itemId, externalSegmentId, expectedType, target.StartTicks, target.EndTicks, cancellationToken).ConfigureAwait(false))
        {
            return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "The external segment's delete is already journaled.");
        }

        var match = FindUncorrelatedCounterpart(itemRows.Where(s => s.State == SegmentState.Active).ToList(), mode, target.StartTicks, target.EndTicks);

        var affected = new List<SegmentValue>();
        if (match is not null)
        {
            affected.Add(await DeleteOwnedRowAsync(db, match, cancellationToken).ConfigureAwait(false));
        }

        await JournalExternalDeleteAsync(db, itemId, externalSegmentId, expectedType, target.StartTicks, target.EndTicks, cancellationToken).ConfigureAwait(false);
        return new MutationResult(null, affected);
    }

    /// <summary>
    /// Finds the plugin counterpart of a Jellyfin row that shares no plugin id (a row
    /// predating the shared-id scheme, or a foreign-provider row) by mode and
    /// boundaries. Several rows can sit inside the tolerance (1-tick-shifted copies of
    /// the same boundaries), so the closest one wins (an exact match beats a shifted
    /// copy) with the id as a deterministic tie-break instead of enumeration order. A
    /// Jellyfin row can drift further from its plugin counterpart when re-analysis or
    /// edits ran while mirroring was off; the legacy DELETE wire matched mode-wide for
    /// non-commercial types, so that is honored where it is unambiguous: exactly one
    /// candidate row of the mode. Commercials (many per item) keep exact matching.
    /// </summary>
    /// <param name="rows">The item's candidate plugin rows (the caller decides the state filter).</param>
    /// <param name="mode">The mode the Jellyfin row maps to.</param>
    /// <param name="startTicks">The Jellyfin row's start ticks.</param>
    /// <param name="endTicks">The Jellyfin row's end ticks.</param>
    /// <returns>The counterpart, or <see langword="null"/> when none matches.</returns>
    internal static DbSegment? FindUncorrelatedCounterpart(IReadOnlyList<DbSegment> rows, AnalysisMode mode, long startTicks, long endTicks)
    {
        var match = rows
            .Where(s => s.Type == mode
                && Math.Abs(s.StartTicks - startTicks) <= UncorrelatedTickTolerance
                && Math.Abs(s.EndTicks - endTicks) <= UncorrelatedTickTolerance)
            .OrderBy(s => Math.Abs(s.StartTicks - startTicks) + Math.Abs(s.EndTicks - endTicks))
            .ThenBy(s => s.Id)
            .FirstOrDefault();

        if (match is null && mode != AnalysisMode.Commercial)
        {
            var modeRows = rows.Where(s => s.Type == mode).ToList();
            if (modeRows.Count == 1)
            {
                match = modeRows[0];
            }
        }

        return match;
    }

    /// <summary>
    /// Shared delete tail of the id-addressed delete arms for one active tracked row:
    /// the staged delete (tombstone or removal), the mode's analysis-record clear, and
    /// the deleted-value report.
    /// </summary>
    private static async Task<SegmentValue> DeleteOwnedRowAsync(IntroSkipperDbContext db, DbSegment row, CancellationToken cancellationToken)
    {
        var snapshot = StageDelete(db, row);
        await ClearItemAnalysisCoreAsync(db, snapshot.ItemId, snapshot.Type, cancellationToken).ConfigureAwait(false);
        return ToDeletedValue(snapshot);
    }

    /// <summary>
    /// Stages the durable delete of one external Jellyfin row, deduplicated on the
    /// exact validated shape: retries while a projection outage holds must not grow
    /// the journal, while an operation for a different shape coexists on purpose
    /// (the superseded old operation drops harmlessly at apply time).
    /// </summary>
    private static async Task JournalExternalDeleteAsync(IntroSkipperDbContext db, Guid itemId, Guid externalSegmentId, MediaSegmentType expectedType, long startTicks, long endTicks, CancellationToken cancellationToken)
    {
        if (await HasJournaledExternalDeleteAsync(db, itemId, externalSegmentId, expectedType, startTicks, endTicks, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        db.ProjectionExternalOperations.Add(new DbProjectionExternalOperation
        {
            ItemId = itemId,
            ExternalSegmentId = externalSegmentId,
            ExpectedType = expectedType,
            StartTicks = startTicks,
            EndTicks = endTicks
        });
    }

    private static Task<bool> HasJournaledExternalDeleteAsync(IntroSkipperDbContext db, Guid itemId, Guid externalSegmentId, MediaSegmentType expectedType, long startTicks, long endTicks, CancellationToken cancellationToken)
        => db.ProjectionExternalOperations.AnyAsync(
            o => o.ItemId == itemId
                && o.ExternalSegmentId == externalSegmentId
                && o.ExpectedType == expectedType
                && o.StartTicks == startTicks
                && o.EndTicks == endTicks,
            cancellationToken);

    // Id-level variant of the pending-op guard for a target that no longer resolves:
    // the journaled operation may itself be why the row is gone (its delete applied,
    // the item sync behind it still pending), and its recorded shape is then the only
    // shape there is.
    private static Task<bool> HasJournaledExternalDeleteForIdAsync(IntroSkipperDbContext db, Guid itemId, Guid externalSegmentId, MediaSegmentType expectedType, CancellationToken cancellationToken)
        => db.ProjectionExternalOperations.AnyAsync(
            o => o.ItemId == itemId
                && o.ExternalSegmentId == externalSegmentId
                && o.ExpectedType == expectedType,
            cancellationToken);

    // The wire-visible 400 text of the MediaSegmentsApi type contradiction; one
    // template for the correlated and uncorrelated arms so the contract cannot drift.
    private static string TypeMismatchMessage(Guid segmentId, MediaSegmentType actualType, MediaSegmentType expectedType)
        => FormattableString.Invariant($"Segment '{segmentId}' is {actualType}, not the requested type '{expectedType}'.");

    private static SegmentValue ToValue(DbSegment row) => new(row.Id, row.ItemId, row.Type, row.StartTicks, row.EndTicks, row.Source, row.State);

    // A deleted user row is reported as it stood (the row is gone); a deleted automatic
    // row is reported suppressed — the tombstone that now records the intent.
    private static SegmentValue ToDeletedValue(DbSegment snapshot)
        => ToValue(snapshot) with { State = snapshot.Source == SegmentSource.User ? snapshot.State : SegmentState.Suppressed };
}
