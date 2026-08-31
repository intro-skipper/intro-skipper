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
    public async Task<MutationResult> ApplyChangeAsync(SegmentChangeIntent intent, ExternalSegmentTarget? externalTarget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (Validate(intent, externalTarget) is { } rejection)
        {
            return new MutationResult(rejection, []);
        }

        await InitializeAsync().ConfigureAwait(false);
        using var db = _contextFactory.CreateDbContext();
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await MutateAsync(db, intent, externalTarget, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is Rejected)
            {
                // Every outcome is decided before a core persists a mutation, so
                // disposing the transaction unwinds nothing that matters.
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
    /// Shape validation plus the external-target ownership checks; every rejection an
    /// intent can earn is produced here, before the transaction opens — except the
    /// editor delete's target checks, which depend on whether a plugin row owns the id
    /// and therefore live in its <see cref="MutateAsync"/> dispatch.
    /// </summary>
    private static Rejected? Validate(SegmentChangeIntent intent, ExternalSegmentTarget? externalTarget)
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
            DeleteExternalSegmentIntent value when value.ExternalSegmentId == Guid.Empty || AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType) is null => new(SegmentChangeRejectedReason.InvalidExternalIdOrType, "Invalid external segment ID or type."),
            DeleteExternalSegmentIntent when externalTarget is null => new(SegmentChangeRejectedReason.ExternalSegmentNotFound, "External segment was not found."),
            DeleteExternalSegmentIntent value when externalTarget.Id != value.ExternalSegmentId => new(SegmentChangeRejectedReason.ExternalSegmentNotFound, "External target does not correspond to the requested segment ID."),
            DeleteExternalSegmentIntent value when externalTarget.ItemId != value.ItemId => new(SegmentChangeRejectedReason.ExternalItemMismatch, "External segment belongs to another item."),
            DeleteExternalSegmentIntent value when externalTarget.Type != value.ExpectedType => new(SegmentChangeRejectedReason.ExternalTypeMismatch, "External segment type does not match the expected type."),
            EditorDeleteSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            EditorDeleteSegmentIntent value when AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType) is null => new(SegmentChangeRejectedReason.InvalidExternalIdOrType, "Invalid segment type."),
            WriteUserTimestampsIntent value when value.Timestamps is null || value.Timestamps.Count == 0 || value.Timestamps.Any(timestamp => !ValidMode(timestamp.Mode) || !TickConversions.IsValidTickRange(timestamp.StartTicks, timestamp.EndTicks)) || value.Timestamps.Select(timestamp => timestamp.Mode).Distinct().Count() != value.Timestamps.Count => new(SegmentChangeRejectedReason.InvalidUserTimestamps, "User timestamps must contain unique supported modes and valid ranges."),
            SegmentVisibilityChangeIntent value when value.SeasonId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySeasonId, "Season ID must not be empty."),
            _ => null
        };
    }

    /// <summary>
    /// Dispatches one validated intent to the shared mutation cores. Prechecks decide
    /// every Ignored/Rejected outcome before any core persists a change, so the caller
    /// can abandon the transaction on a non-null outcome.
    /// </summary>
    private static async Task<MutationResult> MutateAsync(IntroSkipperDbContext db, SegmentChangeIntent intent, ExternalSegmentTarget? externalTarget, CancellationToken cancellationToken)
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
                    var snapshot = await DeleteSegmentCoreAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                    if (snapshot is null)
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "Segment was not found on the item or was already deleted.");
                    }

                    await ClearItemAnalysisCoreAsync(db, value.ItemId, snapshot.Type, cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, [ToDeletedValue(snapshot)]);
                }

            case RestoreSegmentIntent value:
                {
                    var restored = await RestoreSegmentCoreAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                    return restored is null
                        ? MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrNotSuppressed, "Segment was not found on the item or was not suppressed.")
                        : new MutationResult(null, [ToValue(restored)]);
                }

            case DeleteExternalSegmentIntent value:
                // Validate guaranteed the target and its ownership.
                return await DeleteExternalRowAsync(db, value.ItemId, value.ExternalSegmentId, value.ExpectedType, externalTarget!, cancellationToken).ConfigureAwait(false);

            case EditorDeleteSegmentIntent value:
                {
                    // The editor's legacy delete dispatch, decided inside the
                    // transaction so a concurrent mutation cannot invalidate the
                    // chosen path: a plugin row sharing the id is deleted
                    // authoritatively (its mirrored row converges away on
                    // projection), and only an uncorrelated id falls back to the
                    // exactly validated external delete.
                    var mode = AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType)!.Value;
                    var correlated = await db.Segments.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.ItemId == value.ItemId && s.Id == value.SegmentId, cancellationToken)
                        .ConfigureAwait(false);
                    if (correlated is not null)
                    {
                        if (correlated.Type != mode)
                        {
                            return MutationResult.Reject(
                                SegmentChangeRejectedReason.ExternalTypeMismatch,
                                FormattableString.Invariant($"Segment '{value.SegmentId}' is {AnalysisHelpers.ModeToSegmentType[correlated.Type]}, not the requested type '{value.ExpectedType}'."));
                        }

                        if (correlated.State == SegmentState.Suppressed)
                        {
                            // The plugin already treats the row as deleted, so the
                            // delete is idempotently satisfied; the journaled
                            // re-projection removes any ghost row Jellyfin re-added.
                            return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "The segment is already deleted.");
                        }

                        var snapshot = await DeleteSegmentCoreAsync(db, value.ItemId, value.SegmentId, cancellationToken).ConfigureAwait(false);
                        if (snapshot is null)
                        {
                            return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "The segment is already deleted.");
                        }

                        await ClearItemAnalysisCoreAsync(db, value.ItemId, mode, cancellationToken).ConfigureAwait(false);
                        return new MutationResult(null, [ToDeletedValue(snapshot)]);
                    }

                    // No plugin row owns the id, so the resolved Jellyfin row must
                    // corroborate the request before the external delete runs — the
                    // same checks Validate applies to DeleteExternalSegmentIntent.
                    if (externalTarget is null || externalTarget.Id != value.SegmentId)
                    {
                        return MutationResult.Reject(SegmentChangeRejectedReason.ExternalSegmentNotFound, "External segment was not found.");
                    }

                    if (externalTarget.ItemId != value.ItemId)
                    {
                        return MutationResult.Reject(SegmentChangeRejectedReason.ExternalItemMismatch, "External segment belongs to another item.");
                    }

                    if (externalTarget.Type != value.ExpectedType)
                    {
                        return MutationResult.Reject(
                            SegmentChangeRejectedReason.ExternalTypeMismatch,
                            FormattableString.Invariant($"Segment '{value.SegmentId}' is {externalTarget.Type}, not the requested type '{value.ExpectedType}'."));
                    }

                    return await DeleteExternalRowAsync(db, value.ItemId, value.SegmentId, value.ExpectedType, externalTarget, cancellationToken).ConfigureAwait(false);
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
    /// Deletes one exactly validated Jellyfin row and its plugin counterpart. The
    /// counterpart is matched like the editor's legacy delete dispatch: the shared id
    /// first — resolved state-agnostically, so a suppressed shared-id row means the
    /// plugin already treats the segment as deleted and the journaled op merely
    /// removes the lingering ghost row, with no fallback allowed to claim another
    /// (possibly user) segment — then the uncorrelated rule (1-tick tolerance,
    /// non-commercial mode-wide fallback), without which a pre-shared-id row sitting
    /// one tick off would stay active and the very sync this change journals would
    /// resurrect the deleted segment. The journaled operation removes the foreign row
    /// either way, carrying the validated boundaries for the apply-time guard.
    /// </summary>
    private static async Task<MutationResult> DeleteExternalRowAsync(IntroSkipperDbContext db, Guid itemId, Guid externalSegmentId, MediaSegmentType expectedType, ExternalSegmentTarget target, CancellationToken cancellationToken)
    {
        var mode = AnalysisHelpers.TryMapSegmentTypeToMode(expectedType)!.Value;
        var itemRows = await db.Segments.AsNoTracking()
            .Where(s => s.ItemId == itemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var sharedIdRow = itemRows.Find(s => s.Id == externalSegmentId);

        // A pending journaled delete of this uncorrelated row means a concurrent or
        // retried request already recorded this exact delete, and its projection has
        // not applied yet (applies run under the same stripe the caller holds, so
        // none can be mid-flight). Matching again would be dangerous, not just
        // redundant: the first request's counterpart may be gone without a trace (a
        // hard-deleted user row), leaving the mode-wide fallback free to claim a
        // segment the caller never addressed. The pending operation already removes
        // the external row, so the intent is idempotently satisfied.
        if (sharedIdRow is null
            && await db.ProjectionExternalOperations
                .AnyAsync(o => o.ItemId == itemId && o.ExternalSegmentId == externalSegmentId, cancellationToken)
                .ConfigureAwait(false))
        {
            return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, "The external segment's delete is already journaled.");
        }

        var match = sharedIdRow is not null
            ? (sharedIdRow.State == SegmentState.Active && sharedIdRow.Type == mode ? sharedIdRow : null)
            : UncorrelatedSegmentMatcher.Find(
                [.. itemRows.Where(s => s.State == SegmentState.Active)],
                mode,
                target.StartTicks,
                target.EndTicks);

        var affected = new List<SegmentValue>();
        if (match is not null && await DeleteSegmentCoreAsync(db, itemId, match.Id, cancellationToken).ConfigureAwait(false) is { } snapshot)
        {
            affected.Add(ToDeletedValue(snapshot));
            await ClearItemAnalysisCoreAsync(db, itemId, mode, cancellationToken).ConfigureAwait(false);
        }

        db.ProjectionExternalOperations.Add(new DbProjectionExternalOperation
        {
            ItemId = itemId,
            ExternalSegmentId = externalSegmentId,
            ExpectedType = expectedType,
            StartTicks = target.StartTicks,
            EndTicks = target.EndTicks
        });
        return new MutationResult(null, affected);
    }

    private static SegmentValue ToValue(DbSegment row) => new(row.Id, row.ItemId, row.Type, row.StartTicks, row.EndTicks, row.Source, row.State);

    // A deleted user row is reported as it stood (the row is gone); a deleted automatic
    // row is reported suppressed — the tombstone that now records the intent.
    private static SegmentValue ToDeletedValue(DbSegment snapshot)
        => ToValue(snapshot) with { State = snapshot.Source == SegmentSource.User ? snapshot.State : SegmentState.Suppressed };
}
