// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Intent-based change application of <see cref="IntroSkipperDatabase"/>: one
/// transaction that runs the mutation cores the public single-shot methods also use
/// and journals the resulting projection work (a per-item queue marker, plus durable
/// foreign-row deletes), so a committed change can never lose its projection to a
/// crash. The journal records work, not data — projection re-derives the item's image
/// from current truth when it runs.
/// </summary>
public sealed partial class IntroSkipperDatabase
{
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
    private static async Task EnqueueProjectionAsync(IntroSkipperDbContext db, Guid itemId, CancellationToken cancellationToken)
    {
        var queue = await db.ProjectionQueue.FirstOrDefaultAsync(q => q.ItemId == itemId, cancellationToken).ConfigureAwait(false);
        if (queue is null)
        {
            db.ProjectionQueue.Add(new DbProjectionQueueItem { ItemId = itemId, Version = 1 });
        }
        else
        {
            queue.Version++;
            queue.NextAttemptAt = null;
        }
    }

    /// <summary>
    /// Bulk form of <see cref="EnqueueProjectionAsync"/> for analysis and maintenance
    /// writes that change many items' servable state in one transaction: one read
    /// resolves the existing markers, their versions bump, missing markers insert.
    /// The caller saves and commits; the projection worker's poll picks the markers
    /// up, so bulk writers never await Jellyfin.
    /// </summary>
    private static async Task EnqueueProjectionsAsync(IntroSkipperDbContext db, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        var ids = itemIds.Distinct().ToArray();
        var existing = await db.ProjectionQueue
            .Where(q => EF.Parameter(ids).Contains(q.ItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var queue in existing)
        {
            queue.Version++;
            queue.NextAttemptAt = null;
        }

        var known = existing.Select(q => q.ItemId).ToHashSet();
        db.ProjectionQueue.AddRange(ids.Where(id => !known.Contains(id)).Select(id => new DbProjectionQueueItem { ItemId = id, Version = 1 }));
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
            WriteUserTimestampsIntent value when value.Timestamps is null || value.Timestamps.Count == 0 || value.Timestamps.Any(timestamp => !ValidMode(timestamp.Mode) || !TickConversions.IsValidTickRange(timestamp.StartTicks, timestamp.EndTicks)) || value.Timestamps.Select(timestamp => timestamp.Mode).Distinct().Count() != value.Timestamps.Count => new(SegmentChangeRejectedReason.InvalidUserTimestamps, "User timestamps must contain unique supported modes and valid ranges."),
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
                    var exact = await db.Segments.AsNoTracking()
                        .FirstOrDefaultAsync(
                            s => s.ItemId == value.ItemId && s.Type == value.Mode && s.StartTicks == value.StartTicks && s.EndTicks == value.EndTicks,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (exact is { Source: SegmentSource.User, State: SegmentState.Active })
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.UserSegmentAlreadyExists, "The user segment already exists.", [ToValue(exact)]);
                    }

                    var row = await AddUserSegmentCoreAsync(db, value.ItemId, value.Mode, value.StartTicks, value.EndTicks, cancellationToken).ConfigureAwait(false);
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

                    var survivors = await ReplaceUserSegmentsCoreAsync(
                        db,
                        value.ItemId,
                        new Dictionary<AnalysisMode, IReadOnlyList<(long StartTicks, long EndTicks)>> { [value.Mode] = requested },
                        cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, survivors.Select(ToValue).ToList());
                }

            case UpdateSegmentIntent value:
                {
                    var row = await db.Segments.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.ItemId == value.ItemId && s.Id == value.SegmentId, cancellationToken)
                        .ConfigureAwait(false);
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
                    var updated = await UpdateSegmentCoreAsync(db, value.ItemId, value.SegmentId, value.StartTicks, value.EndTicks, cancellationToken).ConfigureAwait(false);
                    return updated is null
                        ? MutationResult.Reject(SegmentChangeRejectedReason.SegmentMissingOrSuppressed, "Segment was not found on the item or is suppressed.")
                        : new MutationResult(null, [ToValue(updated)]);
                }

            case DeleteSegmentIntent value:
                {
                    var deleted = await DeleteOwnedRowAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                    if (deleted is null)
                    {
                        // A suppressed row keeps the journaled re-projection (its
                        // ghost Jellyfin row may linger); an id that exists in no
                        // state has nothing to heal.
                        var suppressed = await db.Segments
                            .AnyAsync(s => s.ItemId == value.ItemId && s.Id == value.SegmentId, cancellationToken)
                            .ConfigureAwait(false);
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "Segment was not found on the item or was already deleted.", reproject: suppressed);
                    }

                    // Like the editor delete's correlated arm: journal the Jellyfin
                    // twin's targeted delete with the row's own shape, so a retry of
                    // this delete through any surface answers idempotently via the
                    // pending-op guard while the sync is still pending.
                    await JournalExternalDeleteAsync(db, value.ItemId, value.SegmentId, AnalysisHelpers.ModeToSegmentType[deleted.Mode], deleted.StartTicks, deleted.EndTicks, cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, [deleted]);
                }

            case RestoreSegmentIntent value:
                {
                    var restored = await RestoreSegmentCoreAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                    if (restored is null)
                    {
                        // Same classification as the delete: an existing (active) row
                        // keeps the healing re-projection, a missing id journals nothing.
                        var exists = await db.Segments
                            .AnyAsync(s => s.ItemId == value.ItemId && s.Id == value.SegmentId, cancellationToken)
                            .ConfigureAwait(false);
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrNotSuppressed, "Segment was not found on the item or was not suppressed.", reproject: exists);
                    }

                    return new MutationResult(null, [ToValue(restored)]);
                }

            case EditorDeleteSegmentIntent value:
                {
                    // The delete dispatch, decided entirely inside the transaction so
                    // a concurrent mutation cannot invalidate the chosen path: a
                    // plugin row sharing the id is deleted authoritatively, and only
                    // an uncorrelated id resolves and validates the external row.
                    var mode = AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType)!.Value;
                    var itemRows = await db.Segments.AsNoTracking()
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

                        var deleted = await DeleteOwnedRowAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                        return deleted is null
                            ? MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "The segment is already deleted.")
                            : new MutationResult(null, [deleted]);
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

            case WriteUserTimestampsIntent value:
                {
                    var modes = value.Timestamps.Select(timestamp => timestamp.Mode).ToArray();
                    var rows = await db.Segments.AsNoTracking()
                        .Where(s => s.ItemId == value.ItemId && modes.Contains(s.Type))
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var allMatch = value.Timestamps.All(timestamp =>
                        rows.Where(s => s.Type == timestamp.Mode && s.State == SegmentState.Active).ToList()
                            is [{ Source: SegmentSource.User } single]
                        && single.StartTicks == timestamp.StartTicks
                        && single.EndTicks == timestamp.EndTicks);
                    if (allMatch)
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.UserImageAlreadyExists, "The requested user timestamps are already stored.");
                    }

                    var byMode = value.Timestamps.ToDictionary(
                        timestamp => timestamp.Mode,
                        timestamp => (IReadOnlyList<(long StartTicks, long EndTicks)>)new[] { (timestamp.StartTicks, timestamp.EndTicks) });
                    var survivors = await ReplaceUserSegmentsCoreAsync(db, value.ItemId, byMode, cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, survivors.Select(ToValue).ToList());
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

        var match = UncorrelatedSegmentMatcher.Find(
            [.. itemRows.Where(s => s.State == SegmentState.Active)],
            mode,
            target.StartTicks,
            target.EndTicks);

        var affected = new List<SegmentValue>();
        if (match is not null && await DeleteOwnedRowAsync(db, itemId, match.Id, cancellationToken).ConfigureAwait(false) is { } deleted)
        {
            affected.Add(deleted);
        }

        await JournalExternalDeleteAsync(db, itemId, externalSegmentId, expectedType, target.StartTicks, target.EndTicks, cancellationToken).ConfigureAwait(false);
        return new MutationResult(null, affected);
    }

    /// <summary>
    /// Shared delete tail of the id-addressed delete arms: the core delete (tombstone
    /// or removal), the mode's analysis-record clear, and the deleted-value report.
    /// Returns <see langword="null"/> when the id is unknown on the item or already
    /// suppressed; nothing is persisted then.
    /// </summary>
    private static async Task<SegmentValue?> DeleteOwnedRowAsync(IntroSkipperDbContext db, Guid itemId, Guid segmentId, CancellationToken cancellationToken)
    {
        var snapshot = await DeleteSegmentCoreAsync(db, itemId, segmentId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        await ClearItemAnalysisCoreAsync(db, itemId, snapshot.Type, cancellationToken).ConfigureAwait(false);
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
