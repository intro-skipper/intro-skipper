// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
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
            if (result.Outcome is not null)
            {
                // Every Ignored/Rejected outcome is decided before a core persists a
                // mutation, so disposing the transaction unwinds nothing that matters.
                return result;
            }

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
    /// intent can earn is produced here, before the transaction opens.
    /// </summary>
    private static Rejected? Validate(SegmentChangeIntent intent, ExternalSegmentTarget? externalTarget)
    {
        static bool ValidMode(AnalysisMode mode) => AnalysisHelpers.IsSupported(mode);
        static bool ValidRange(long start, long end) => start >= 0 && end > start;

        if (intent.ItemId == Guid.Empty)
        {
            return new(SegmentChangeRejectedReason.EmptyItemId, "Item ID must not be empty.");
        }

        return intent switch
        {
            AddUserSegmentIntent value when !ValidMode(value.Mode) || !ValidRange(value.StartTicks, value.EndTicks) => new(SegmentChangeRejectedReason.InvalidModeOrRange, "Invalid mode or tick range."),
            ReplaceUserSegmentsForModeIntent value when value.Segments is null || !ValidMode(value.Mode) || value.Segments.Any(range => !ValidRange(range.StartTicks, range.EndTicks)) => new(SegmentChangeRejectedReason.InvalidModeOrRange, "Invalid mode or tick range."),
            UpdateSegmentIntent value when value.SegmentId == Guid.Empty || !ValidRange(value.StartTicks, value.EndTicks) => new(SegmentChangeRejectedReason.InvalidSegmentIdOrRange, "Invalid segment ID or tick range."),
            DeleteSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            RestoreSegmentIntent value when value.SegmentId == Guid.Empty => new(SegmentChangeRejectedReason.EmptySegmentId, "Segment ID must not be empty."),
            DeleteExternalSegmentIntent value when value.ExternalSegmentId == Guid.Empty || AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType) is null => new(SegmentChangeRejectedReason.InvalidExternalIdOrType, "Invalid external segment ID or type."),
            DeleteExternalSegmentIntent when externalTarget is null => new(SegmentChangeRejectedReason.ExternalSegmentNotFound, "External segment was not found."),
            DeleteExternalSegmentIntent value when externalTarget.ItemId != value.ItemId => new(SegmentChangeRejectedReason.ExternalItemMismatch, "External segment belongs to another item."),
            DeleteExternalSegmentIntent value when externalTarget.Type != value.ExpectedType => new(SegmentChangeRejectedReason.ExternalTypeMismatch, "External segment type does not match the expected type."),
            WriteUserTimestampsIntent value when value.Timestamps is null || value.Timestamps.Count == 0 || value.Timestamps.Any(timestamp => !ValidMode(timestamp.Mode) || !ValidRange(timestamp.StartTicks, timestamp.EndTicks)) || value.Timestamps.Select(timestamp => timestamp.Mode).Distinct().Count() != value.Timestamps.Count => new(SegmentChangeRejectedReason.InvalidUserTimestamps, "User timestamps must contain unique supported modes and valid ranges."),
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
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.UserSegmentAlreadyExists, "The user segment already exists.");
                    }

                    var row = await AddUserSegmentCoreAsync(db, value.ItemId, value.Mode, value.StartTicks, value.EndTicks, cancellationToken).ConfigureAwait(false);
                    return new MutationResult(null, [ToValue(row)]);
                }

            case ReplaceUserSegmentsForModeIntent value:
                {
                    var requested = value.Segments.Select(range => (range.StartTicks, range.EndTicks)).Distinct().ToList();
                    var active = await db.Segments.AsNoTracking()
                        .Where(s => s.ItemId == value.ItemId && s.Type == value.Mode && s.State == SegmentState.Active)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (active.Count == requested.Count && active.All(row => row.Source == SegmentSource.User && requested.Contains((row.StartTicks, row.EndTicks))))
                    {
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.UserImageAlreadyExists, "The requested user image already exists.");
                    }

                    if (requested.Count == 0)
                    {
                        // An empty set is the mode-wide delete, with delete semantics:
                        // automatic rows tombstone (so re-analysis cannot resurrect
                        // them), user rows go for good, and the mode's analysis record
                        // clears so the next scan may look for other segments.
                        var affected = new List<SegmentValue>();
                        foreach (var row in await db.Segments
                            .Where(s => s.ItemId == value.ItemId && s.Type == value.Mode && s.State == SegmentState.Active)
                            .ToListAsync(cancellationToken)
                            .ConfigureAwait(false))
                        {
                            if (row.Source == SegmentSource.User)
                            {
                                db.Segments.Remove(row);
                            }
                            else
                            {
                                row.State = SegmentState.Suppressed;
                            }

                            affected.Add(ToDeletedValue(row));
                        }

                        await ClearItemAnalysisCoreAsync(db, value.ItemId, value.Mode, cancellationToken).ConfigureAwait(false);
                        return new MutationResult(null, affected);
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
                        return MutationResult.Ignore(SegmentChangeIgnoredReason.SegmentAlreadyHasValues, "The segment already has the requested values.");
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
                {
                    // Validate guaranteed the target and its ownership. Only an exact
                    // counterpart (same mode and boundaries) is deleted locally; the
                    // journaled operation removes the foreign row either way.
                    var mode = AnalysisHelpers.TryMapSegmentTypeToMode(value.ExpectedType)!.Value;
                    var affected = new List<SegmentValue>();
                    var match = await db.Segments.AsNoTracking()
                        .FirstOrDefaultAsync(
                            s => s.ItemId == value.ItemId && s.Type == mode && s.StartTicks == externalTarget!.StartTicks && s.EndTicks == externalTarget.EndTicks && s.State == SegmentState.Active,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (match is not null && await DeleteSegmentCoreAsync(db, value.ItemId, match.Id, cancellationToken).ConfigureAwait(false) is { } snapshot)
                    {
                        affected.Add(ToDeletedValue(snapshot));
                        await ClearItemAnalysisCoreAsync(db, value.ItemId, mode, cancellationToken).ConfigureAwait(false);
                    }

                    db.ProjectionExternalOperations.Add(new DbProjectionExternalOperation
                    {
                        ItemId = value.ItemId,
                        ExternalSegmentId = value.ExternalSegmentId,
                        ExpectedType = value.ExpectedType
                    });
                    return new MutationResult(null, affected);
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

    private static SegmentValue ToValue(DbSegment row) => new(row.Id, row.ItemId, row.Type, row.StartTicks, row.EndTicks, row.Source, row.State);

    // A deleted user row is reported as it stood (the row is gone); a deleted automatic
    // row is reported suppressed — the tombstone that now records the intent.
    private static SegmentValue ToDeletedValue(DbSegment snapshot)
        => ToValue(snapshot) with { State = snapshot.Source == SegmentSource.User ? snapshot.State : SegmentState.Suppressed };
}
