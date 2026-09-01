// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>Durability and semantics tests for the segment change coordinator.</summary>
public sealed class TestSegmentChange : IDisposable
{
    private readonly string _dbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-changes.db");

    [Fact]
    public async Task AddUserSegment_CommitsImageAndCompletesQueuedWork()
    {
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);
        var itemId = Guid.NewGuid();

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        Assert.Equal(ProjectionState.Applied, outcome.Projection);
        var affected = Assert.Single(outcome.AffectedValues);
        var applied = Assert.Single(adapter.Applies);
        var projected = Assert.Single(applied.Segments);
        Assert.Equal(affected.Id, projected.Id);
        Assert.Equal(SegmentSource.User, projected.Source);

        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task ProjectionFailure_DoesNotRollBackAuthoritativeMutation_AndManualRetryConverges()
    {
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        var service = CreateService(adapter);
        var itemId = Guid.NewGuid();

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40)));
        Assert.Equal(ProjectionState.Pending, outcome.Projection);

        await using (var db = CreateContext())
        {
            Assert.Single(await db.Segments.ToListAsync());
            var queued = Assert.Single(await db.ProjectionQueue.ToListAsync());
            Assert.Equal(1, queued.AttemptCount);
            Assert.NotNull(queued.Failure);
            Assert.NotNull(queued.NextAttemptAt);
        }

        Assert.Equal(1, (await service.RetryProjectionAsync(ProjectionScope.ForItem(itemId))).RetriedCount);
        Assert.Equal(2, adapter.Attempts.Count);
        Assert.Equal(ProjectionState.Applied, Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items).State);
    }

    [Fact]
    public async Task CoalescedWork_AppliesLatestTruthOnce()
    {
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 2 };
        var service = CreateService(adapter);
        var itemId = Guid.NewGuid();

        await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20));
        await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40));

        // Both immediate attempts failed; the work coalesced into one marker.
        Assert.Equal(2, adapter.Attempts.Count);
        await using (var db = CreateContext())
        {
            Assert.Single(await db.ProjectionQueue.ToListAsync());
        }

        // One retry applies the item's current truth — both segments in one pass.
        Assert.Equal(1, (await service.RetryProjectionAsync(ProjectionScope.ForItem(itemId))).RetriedCount);
        Assert.Equal(2, Assert.Single(adapter.Applies).Segments.Count);
        await using var verify = CreateContext();
        Assert.Empty(await verify.ProjectionQueue.ToListAsync());
    }

    [Fact]
    public async Task DeleteAutomaticSegment_PersistsTombstoneAndProjectsEmptyImage()
    {
        var itemId = Guid.NewGuid();
        var automatic = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter);
        await SeedAsync(automatic);
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(new DeleteSegmentIntent(itemId, automatic.Id)));

        Assert.Equal(SegmentState.Suppressed, Assert.Single(outcome.AffectedValues).State);
        Assert.Empty(Assert.Single(adapter.Applies).Segments);
        await using var db = CreateContext();
        Assert.Equal(SegmentState.Suppressed, Assert.Single(await db.Segments.ToListAsync()).State);
    }

    [Fact]
    public async Task EditorDelete_CorrelatedActiveRow_JournalsItsTwinRowsTargetedDelete()
    {
        var itemId = Guid.NewGuid();
        var row = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.User);
        await SeedAsync(row);
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, row.Id, MediaSegmentType.Intro)));

        // The plugin row owns the id: the delete is authoritative, and its Jellyfin
        // twin's targeted delete is journaled with the row's own shape — the durable
        // record that lets a retry answer idempotently while the sync is pending.
        Assert.Equal(ProjectionState.Applied, outcome.Projection);
        Assert.Equal(row.Id, Assert.Single(outcome.AffectedValues).Id);
        var operation = Assert.Single(Assert.Single(adapter.Applies).ExternalOperations);
        Assert.Equal(row.Id, operation.ExternalSegmentId);
        Assert.Equal(10, operation.StartTicks);
        await using var db = CreateContext();
        Assert.Empty(await db.Segments.ToListAsync());
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_RetriedCorrelatedDelete_CannotClaimAnotherSegment()
    {
        var itemId = Guid.NewGuid();

        // The correlated delete hard-deletes user intro A without a trace and its
        // sync stays pending; the retry resolves the still-mirrored twin as an
        // uncorrelated row. The journaled targeted delete must answer it
        // idempotently — without it, the mode-wide fallback would claim B.
        var claimed = new DbSegment(itemId, AnalysisMode.Introduction, 1000, 2000, SegmentSource.User);
        var survivor = new DbSegment(itemId, AnalysisMode.Introduction, 5000, 6000, SegmentSource.User);
        await SeedAsync(claimed, survivor);
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        adapter.ExternalTargets[claimed.Id] = new ExternalSegmentTarget(claimed.Id, itemId, MediaSegmentType.Intro, 1000, 2000);
        var service = CreateService(adapter);

        var first = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, claimed.Id, MediaSegmentType.Intro)));
        Assert.Equal(ProjectionState.Pending, first.Projection);

        var second = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, claimed.Id, MediaSegmentType.Intro)));

        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, second.Reason);
        Assert.Equal(claimed.Id, Assert.Single(Assert.Single(adapter.Applies).ExternalOperations).ExternalSegmentId);
        await using var db = CreateContext();
        Assert.Equal(survivor.Id, Assert.Single(await db.Segments.ToListAsync()).Id);
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_RewrittenExternalRow_JournalsAFreshOperation()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        adapter.ExternalTargets[externalId] = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20);
        var service = CreateService(adapter);

        Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        // The foreign provider rewrote the row under its stable id while the first
        // delete's projection was pending: the re-delete must journal a fresh
        // operation for the new shape instead of reporting the old one as covering
        // it (the old operation drops harmlessly as superseded at apply time).
        adapter.ExternalTargets[externalId] = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 25);
        Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        var operations = Assert.Single(adapter.Applies).ExternalOperations;
        Assert.Equal(2, operations.Count);
        Assert.Contains(operations, o => o.EndTicks == 20);
        Assert.Contains(operations, o => o.EndTicks == 25);
        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task ModeWideFallback_HealsDrift_EvenWithUnrelatedPendingWork()
    {
        var itemId = Guid.NewGuid();
        var drifted = new DbSegment(itemId, AnalysisMode.Introduction, 5000, 6000, SegmentSource.Chapter);
        await SeedAsync(drifted);
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        var service = CreateService(adapter);

        // An unrelated failed change leaves the item's marker pending. The
        // fallback's drift heal must still work — the retry hazards it once posed
        // are answered by the pending-op guard (every single-row delete journals
        // its target's operation), not by suppressing the heal.
        Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40)));

        var externalId = Guid.NewGuid();
        adapter.ExternalTargets[externalId] = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 100, 200);
        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        // The mode's single active intro is the legacy mode-scoped match and is
        // tombstoned alongside the journaled foreign-row delete.
        Assert.Equal(drifted.Id, Assert.Single(outcome.AffectedValues).Id);
        await using var db = CreateContext();
        Assert.Equal(SegmentState.Suppressed, Assert.Single(await db.Segments.ToListAsync(), s => s.Id == drifted.Id).State);
    }

    [Fact]
    public async Task PluralDelete_RetriedThroughTheEditor_IsIgnored()
    {
        var itemId = Guid.NewGuid();

        // The plural API hard-deletes user intro A; its sync stays pending, so the
        // editor still lists the mirrored twin and the user deletes it there. The
        // plural delete's journaled twin operation answers the cross-surface retry
        // idempotently — without it, the mode-wide fallback would claim B.
        var claimed = new DbSegment(itemId, AnalysisMode.Introduction, 1000, 2000, SegmentSource.User);
        var survivor = new DbSegment(itemId, AnalysisMode.Introduction, 5000, 6000, SegmentSource.User);
        await SeedAsync(claimed, survivor);
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        adapter.ExternalTargets[claimed.Id] = new ExternalSegmentTarget(claimed.Id, itemId, MediaSegmentType.Intro, 1000, 2000);
        var service = CreateService(adapter);

        var first = Assert.IsType<Accepted>(await service.ApplyAsync(new DeleteSegmentIntent(itemId, claimed.Id)));
        Assert.Equal(ProjectionState.Pending, first.Projection);

        var second = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, claimed.Id, MediaSegmentType.Intro)));

        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, second.Reason);
        await using var db = CreateContext();
        Assert.Equal(survivor.Id, Assert.Single(await db.Segments.ToListAsync()).Id);
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task DeleteAndRestoreOfUnknownId_JournalNothing()
    {
        var itemId = Guid.NewGuid();
        await SeedAsync(new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.User));
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);

        // Ids that exist in no state have nothing to heal: the 404-style probes
        // must not pay a journal write and a mirror sync.
        Assert.IsType<Ignored>(await service.ApplyAsync(new DeleteSegmentIntent(itemId, Guid.NewGuid())));
        Assert.IsType<Ignored>(await service.ApplyAsync(new RestoreSegmentIntent(itemId, Guid.NewGuid())));

        Assert.Empty(adapter.Attempts);
        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_CorrelatedRow_NeedsNoJellyfinResolution()
    {
        var itemId = Guid.NewGuid();
        var row = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.User);
        await SeedAsync(row);
        var adapter = new RecordingProjectionAdapter { ResolveException = new InvalidOperationException("jellyfin down") };
        var service = CreateService(adapter);

        // A plugin row owns the id, so the dispatch is decided authoritatively: the
        // failing Jellyfin resolution is never consulted and cannot block the delete.
        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, row.Id, MediaSegmentType.Intro)));

        Assert.Equal(row.Id, Assert.Single(outcome.AffectedValues).Id);
        await using var db = CreateContext();
        Assert.Empty(await db.Segments.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_CorrelatedTypeMismatch_RejectsWithActualType()
    {
        var itemId = Guid.NewGuid();
        var credits = new DbSegment(itemId, AnalysisMode.Credits, 30, 40, SegmentSource.Chapter);
        await SeedAsync(credits);
        var service = CreateService(new RecordingProjectionAdapter());

        var rejected = Assert.IsType<Rejected>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, credits.Id, MediaSegmentType.Intro)));

        Assert.Equal(SegmentChangeRejectedReason.ExternalTypeMismatch, rejected.Reason);
        Assert.Contains(nameof(MediaSegmentType.Outro), rejected.Message, StringComparison.Ordinal);
        await using var db = CreateContext();
        Assert.Equal(SegmentState.Active, Assert.Single(await db.Segments.ToListAsync()).State);
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_SuppressedCorrelatedRow_IsIgnoredButStillReprojects()
    {
        var itemId = Guid.NewGuid();
        var tombstone = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter)
        {
            State = SegmentState.Suppressed,
        };
        await SeedAsync(tombstone);
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);

        var ignored = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, tombstone.Id, MediaSegmentType.Intro)));

        // The plugin already treats the row as deleted; the journaled re-projection
        // and the tombstone's targeted delete are what remove a ghost Jellyfin row
        // re-added since.
        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, ignored.Reason);
        var applied = Assert.Single(adapter.Applies);
        Assert.Empty(applied.Segments);
        Assert.Equal(tombstone.Id, Assert.Single(applied.ExternalOperations).ExternalSegmentId);
        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_Uncorrelated_MatchesWithinOneTick_AndJournalsForeignDelete()
    {
        var itemId = Guid.NewGuid();

        // Imported rows are rounded from seconds; the pre-upgrade Jellyfin row of the
        // same value was truncated one tick lower and carries its own id.
        var pluginRow = new DbSegment(itemId, AnalysisMode.Introduction, 1238398, 500000000, SegmentSource.User);
        await SeedAsync(pluginRow);
        var jellyfinRowId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        adapter.ExternalTargets[jellyfinRowId] = new ExternalSegmentTarget(jellyfinRowId, itemId, MediaSegmentType.Intro, 1238397, 500000000);
        var service = CreateService(adapter);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, jellyfinRowId, MediaSegmentType.Intro)));

        // The one-tick-off counterpart is deleted so the very sync this change
        // journals cannot resurrect the segment, and the foreign row's delete is
        // journaled with its validated boundaries.
        Assert.Equal(ProjectionState.Applied, outcome.Projection);
        var operation = Assert.Single(Assert.Single(adapter.Applies).ExternalOperations);
        Assert.Equal(jellyfinRowId, operation.ExternalSegmentId);
        Assert.Equal(1238397, operation.StartTicks);
        await using var db = CreateContext();
        Assert.Empty(await db.Segments.ToListAsync());
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_Uncorrelated_SeveralRowsWithinTolerance_DeletesTheExactMatch()
    {
        var itemId = Guid.NewGuid();

        // Two 1-tick-shifted copies of the same boundaries (truncated and rounded
        // eras) both sit within tolerance; the exact match must win.
        var shiftedCopy = new DbSegment(itemId, AnalysisMode.Introduction, 1238397, 500000000, SegmentSource.User);
        var exactCopy = new DbSegment(itemId, AnalysisMode.Introduction, 1238398, 500000000, SegmentSource.User);
        await SeedAsync(shiftedCopy, exactCopy);
        var jellyfinRowId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        adapter.ExternalTargets[jellyfinRowId] = new ExternalSegmentTarget(jellyfinRowId, itemId, MediaSegmentType.Intro, 1238398, 500000000);
        var service = CreateService(adapter);

        Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, jellyfinRowId, MediaSegmentType.Intro)));

        await using var db = CreateContext();
        Assert.Equal(shiftedCopy.Id, Assert.Single(await db.Segments.ToListAsync()).Id);
    }

    [Fact]
    public async Task EditorDelete_PendingJournaledDelete_IsIgnored_AndClaimsNoFurtherSegment()
    {
        var itemId = Guid.NewGuid();

        // Two active user intros; the first delete of the uncorrelated Jellyfin row
        // hard-deletes its tick-matched counterpart and leaves the foreign-row
        // delete journaled (the immediate projection fails). A repeated delete of
        // the same row — a double-click, a retry, or a request that resolved before
        // the first one's projection ran — must not let the mode-wide fallback
        // claim the surviving intro the user never addressed.
        var claimed = new DbSegment(itemId, AnalysisMode.Introduction, 1000, 2000, SegmentSource.User);
        var survivor = new DbSegment(itemId, AnalysisMode.Introduction, 5000, 6000, SegmentSource.User);
        await SeedAsync(claimed, survivor);
        var jellyfinRowId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        adapter.ExternalTargets[jellyfinRowId] = new ExternalSegmentTarget(jellyfinRowId, itemId, MediaSegmentType.Intro, 1000, 2000);
        var service = CreateService(adapter);

        var first = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, jellyfinRowId, MediaSegmentType.Intro)));
        Assert.Equal(ProjectionState.Pending, first.Projection);

        var second = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, jellyfinRowId, MediaSegmentType.Intro)));

        // The retry's re-projection then converged: the journaled delete ran exactly
        // once, and the surviving intro was never touched.
        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, second.Reason);
        var applied = Assert.Single(adapter.Applies);
        Assert.Equal(jellyfinRowId, Assert.Single(applied.ExternalOperations).ExternalSegmentId);
        await using var db = CreateContext();
        Assert.Equal(survivor.Id, Assert.Single(await db.Segments.ToListAsync()).Id);
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_RetryAfterJournaledDeleteEmptiedJellyfin_IsIgnoredNotRejected()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        adapter.ExternalTargets[externalId] = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20);
        var service = CreateService(adapter);

        var first = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));
        Assert.Equal(ProjectionState.Pending, first.Projection);

        // The journaled operation deletes the Jellyfin row before the item sync, so
        // a failed sync can leave the row gone while the work is still pending.
        adapter.ExternalTargets.Remove(externalId);

        // A probe claiming another type earns no idempotent answer: nothing
        // resolvable corroborates it, and the pending operation records a different
        // delete.
        var otherType = Assert.IsType<Rejected>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Outro)));
        Assert.Equal(SegmentChangeRejectedReason.ExternalSegmentNotFound, otherType.Reason);

        // The true retry answers idempotently from the journal instead of 404ing,
        // and its re-projection converges the pending work.
        var retry = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, retry.Reason);
        Assert.Equal(externalId, Assert.Single(Assert.Single(adapter.Applies).ExternalOperations).ExternalSegmentId);
        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_UncorrelatedResolutionMismatches_RejectBeforeCommit()
    {
        var itemId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);

        // No Jellyfin row resolves under the id.
        var missing = Assert.IsType<Rejected>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, Guid.NewGuid(), MediaSegmentType.Intro)));
        Assert.Equal(SegmentChangeRejectedReason.ExternalSegmentNotFound, missing.Reason);

        // The resolved row belongs to another item.
        var foreignId = Guid.NewGuid();
        adapter.ExternalTargets[foreignId] = new ExternalSegmentTarget(foreignId, Guid.NewGuid(), MediaSegmentType.Intro, 10, 20);
        var foreign = Assert.IsType<Rejected>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, foreignId, MediaSegmentType.Intro)));
        Assert.Equal(SegmentChangeRejectedReason.ExternalItemMismatch, foreign.Reason);

        // The resolved row carries another type.
        var mismatchedId = Guid.NewGuid();
        adapter.ExternalTargets[mismatchedId] = new ExternalSegmentTarget(mismatchedId, itemId, MediaSegmentType.Outro, 10, 20);
        var mismatched = Assert.IsType<Rejected>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, mismatchedId, MediaSegmentType.Intro)));
        Assert.Equal(SegmentChangeRejectedReason.ExternalTypeMismatch, mismatched.Reason);

        // A resolution that does not correspond to the requested id (the facade is a
        // public API and must not trust the resolver's pairing) is rejected too.
        adapter.ExternalTarget = new ExternalSegmentTarget(Guid.NewGuid(), itemId, MediaSegmentType.Intro, 10, 20);
        var mismatchedPairing = Assert.IsType<Rejected>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, Guid.NewGuid(), MediaSegmentType.Intro)));
        Assert.Equal(SegmentChangeRejectedReason.ExternalSegmentNotFound, mismatchedPairing.Reason);

        await using var db = CreateContext();
        await db.ApplyMigrationsAsync();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task IgnoredIdempotentAddAndUpdate_ReportTheExistingRow()
    {
        var itemId = Guid.NewGuid();
        var service = CreateService(new RecordingProjectionAdapter());
        var first = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));
        var rowId = Assert.Single(first.AffectedValues).Id;

        // Idempotent create and same-values update both report the row that already
        // satisfies the intent, so wire adapters can keep their applied shapes.
        var addAgain = Assert.IsType<Ignored>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));
        Assert.Equal(SegmentChangeIgnoredReason.UserSegmentAlreadyExists, addAgain.Reason);
        Assert.Equal(rowId, Assert.Single(addAgain.AffectedValues).Id);

        var updateSame = Assert.IsType<Ignored>(await service.ApplyAsync(
            new UpdateSegmentIntent(itemId, rowId, 10, 20)));
        Assert.Equal(SegmentChangeIgnoredReason.SegmentAlreadyHasValues, updateSame.Reason);
        Assert.Equal(rowId, Assert.Single(updateSame.AffectedValues).Id);
    }

    [Fact]
    public async Task VisibilityFailure_KeepsDisabledFlagAndPendingFilteredImage()
    {
        var itemId = Guid.NewGuid();
        await SeedAsync(
            new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter),
            new DbSegment(itemId, AnalysisMode.Credits, 30, 40, SegmentSource.User));
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        var service = CreateService(adapter);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new SegmentVisibilityChangeIntent(itemId, Guid.NewGuid(), Visible: false)));

        Assert.Equal(ProjectionState.Pending, outcome.Projection);
        Assert.Equal(SegmentSource.User, Assert.Single(Assert.Single(adapter.Attempts).Segments).Source);
        await using var db = CreateContext();
        Assert.True(await db.DisabledItems.AnyAsync(item => item.ItemId == itemId));
    }

    [Fact]
    public async Task MultiModeTimestampWrite_IsAtomicAndUsesUserPrecedence()
    {
        var itemId = Guid.NewGuid();
        await SeedAsync(new DbSegment(itemId, AnalysisMode.Introduction, 1, 2, SegmentSource.Chapter));
        var service = CreateService(new RecordingProjectionAdapter());

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(new WriteUserTimestampsIntent(
            itemId,
            [new UserTimestamp(AnalysisMode.Introduction, 10, 20), new UserTimestamp(AnalysisMode.Credits, 30, 40)])));

        Assert.Equal(2, outcome.AffectedValues.Count);
        await using var db = CreateContext();
        var rows = await db.Segments.OrderBy(row => row.Type).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(SegmentSource.User, row.Source));
    }

    [Fact]
    public async Task IdenticalTimestampRewrite_IsIgnoredAndKeepsIds()
    {
        var itemId = Guid.NewGuid();
        var service = CreateService(new RecordingProjectionAdapter());
        var first = Assert.IsType<Accepted>(await service.ApplyAsync(new WriteUserTimestampsIntent(
            itemId, [new UserTimestamp(AnalysisMode.Introduction, 10, 20)])));

        var second = Assert.IsType<Ignored>(await service.ApplyAsync(new WriteUserTimestampsIntent(
            itemId, [new UserTimestamp(AnalysisMode.Introduction, 10, 20)])));

        Assert.Equal(SegmentChangeIgnoredReason.UserImageAlreadyExists, second.Reason);
        await using var db = CreateContext();
        Assert.Equal(Assert.Single(first.AffectedValues).Id, Assert.Single(await db.Segments.ToListAsync()).Id);
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
    }

    [Fact]
    public async Task AddPromotion_KeepsRowIdAndAnalysisRecord()
    {
        var itemId = Guid.NewGuid();
        var automatic = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter, "automatic-hash");
        await SeedAsync(automatic);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(itemId, AnalysisMode.Introduction, "automatic-hash"));

        var accepted = Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        Assert.Equal(automatic.Id, Assert.Single(accepted.AffectedValues).Id);
        await using var db = CreateContext();
        var row = Assert.Single(await db.Segments.ToListAsync());
        Assert.Equal(SegmentSource.User, row.Source);
        Assert.Equal(string.Empty, row.ConfigHash);
        await AssertAnalyzedItemAsync(itemId, AnalysisMode.Introduction, "automatic-hash");
    }

    [Fact]
    public async Task ReplaceUserSegments_KeepsExactRangeRowInPlace()
    {
        var itemId = Guid.NewGuid();
        var automatic = new DbSegment(itemId, AnalysisMode.Credits, 30, 40, SegmentSource.BlackFrame, "old-hash");
        await SeedAsync(automatic);

        var accepted = Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new ReplaceUserSegmentsForModeIntent(
                itemId,
                AnalysisMode.Credits,
                [new SegmentRange(30, 40), new SegmentRange(50, 60)])));

        Assert.Equal(2, accepted.AffectedValues.Count);
        await using var db = CreateContext();
        var rows = await db.Segments.OrderBy(row => row.StartTicks).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(automatic.Id, rows[0].Id);
        Assert.All(rows, row => Assert.Equal(SegmentSource.User, row.Source));
    }

    [Fact]
    public async Task ReplaceUserSegments_EmptyImage_TombstonesAutosAndClearsAnalysis()
    {
        var itemId = Guid.NewGuid();
        await SeedAsync(
            new DbSegment(itemId, AnalysisMode.Commercial, 10, 20, SegmentSource.User),
            new DbSegment(itemId, AnalysisMode.Commercial, 30, 40, SegmentSource.Chapter, "auto-hash"));
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(itemId, AnalysisMode.Commercial, "auto-hash"));
        var adapter = new RecordingProjectionAdapter();

        Assert.IsType<Accepted>(await CreateService(adapter).ApplyAsync(
            new ReplaceUserSegmentsForModeIntent(itemId, AnalysisMode.Commercial, [])));

        Assert.Empty(Assert.Single(adapter.Applies).Segments);
        await using var db = CreateContext();
        var remaining = Assert.Single(await db.Segments.ToListAsync());
        Assert.Equal(SegmentState.Suppressed, remaining.State);
        Assert.Equal(SegmentSource.Chapter, remaining.Source);
        Assert.Empty(await db.AnalyzedItems.ToListAsync());
    }

    [Fact]
    public async Task UpdateCollision_MergesIntoOccupantKeepingItsId()
    {
        var itemId = Guid.NewGuid();
        var moved = new DbSegment(itemId, AnalysisMode.Preview, 10, 20, SegmentSource.Chapter, "one");
        var occupant = new DbSegment(itemId, AnalysisMode.Preview, 30, 40, SegmentSource.BlackFrame, "two");
        await SeedAsync(moved, occupant);

        var accepted = Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new UpdateSegmentIntent(itemId, moved.Id, occupant.StartTicks, occupant.EndTicks)));

        Assert.Equal(occupant.Id, Assert.Single(accepted.AffectedValues).Id);
        await using var db = CreateContext();
        var row = Assert.Single(await db.Segments.ToListAsync());
        Assert.Equal(occupant.Id, row.Id);
        Assert.Equal(SegmentSource.User, row.Source);
    }

    [Fact]
    public async Task Delete_ClearsAnalysisRecordAndTombstonesAutomaticRow()
    {
        var userItemId = Guid.NewGuid();
        var deletedUser = new DbSegment(userItemId, AnalysisMode.Commercial, 10, 20, SegmentSource.User);
        await SeedAsync(deletedUser);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(userItemId, AnalysisMode.Commercial, "user-mode"));
        var service = CreateService(new RecordingProjectionAdapter());

        await service.ApplyAsync(new DeleteSegmentIntent(userItemId, deletedUser.Id));

        var autoItemId = Guid.NewGuid();
        var deletedAuto = new DbSegment(autoItemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter, "auto-hash");
        await SeedAsync(deletedAuto);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(autoItemId, AnalysisMode.Introduction, "auto-hash"));

        await service.ApplyAsync(new DeleteSegmentIntent(autoItemId, deletedAuto.Id));

        await using var db = CreateContext();
        Assert.Empty(await db.Segments.Where(row => row.ItemId == userItemId).ToListAsync());
        Assert.Equal(SegmentState.Suppressed, Assert.Single(await db.Segments.Where(row => row.ItemId == autoItemId).ToListAsync()).State);
        Assert.Empty(await db.AnalyzedItems.ToListAsync());
    }

    [Fact]
    public async Task RestoreAutomatic_RearmsAnalyzedRecordAndDropsRowHash()
    {
        var itemId = Guid.NewGuid();
        var tombstone = new DbSegment(itemId, AnalysisMode.Credits, 10, 20, SegmentSource.BlackFrame, "restore-hash")
        {
            State = SegmentState.Suppressed
        };
        await SeedAsync(tombstone);

        Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new RestoreSegmentIntent(itemId, tombstone.Id)));

        await AssertAnalyzedItemAsync(itemId, AnalysisMode.Credits, "restore-hash");
        await using var db = CreateContext();
        var row = Assert.Single(await db.Segments.ToListAsync());
        Assert.Equal(SegmentState.Active, row.State);

        // The hash-driven stale cleanup only judges rows carrying a hash; the restored
        // row must not carry one, or the next configuration change deletes it again.
        Assert.Equal(string.Empty, row.ConfigHash);
    }

    [Fact]
    public async Task RestoreAutomatic_KeepsNewerAnalysisRecord()
    {
        var itemId = Guid.NewGuid();
        var tombstone = new DbSegment(itemId, AnalysisMode.Credits, 10, 20, SegmentSource.BlackFrame, "old")
        {
            State = SegmentState.Suppressed
        };
        await SeedAsync(tombstone);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(itemId, AnalysisMode.Credits, "newer"));

        Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new RestoreSegmentIntent(itemId, tombstone.Id)));

        await AssertAnalyzedItemAsync(itemId, AnalysisMode.Credits, "newer");
    }

    [Fact]
    public async Task ExternalDelete_MatchesCounterpartWithinOneTick()
    {
        // A row mirrored before the shared-id scheme can sit one tick from its plugin
        // counterpart; the exact-match miss would resurrect the segment on the very
        // sync this change journals.
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var counterpart = new DbSegment(itemId, AnalysisMode.Introduction, 11, 21, SegmentSource.Chapter);
        await SeedAsync(counterpart);
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20)
        };
        var service = CreateService(adapter);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        Assert.Equal(counterpart.Id, Assert.Single(outcome.AffectedValues).Id);
        await using var db = CreateContext();
        Assert.Equal(SegmentState.Suppressed, Assert.Single(await db.Segments.ToListAsync()).State);
    }

    [Fact]
    public async Task EmptyReplace_OnEmptyMode_ClearsStaleAnalysisRecord()
    {
        var itemId = Guid.NewGuid();
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(itemId, AnalysisMode.Commercial, "stale"));
        var service = CreateService(new RecordingProjectionAdapter());

        // No active rows, but the stale record must still clear so re-detection runs.
        var first = Assert.IsType<Accepted>(await service.ApplyAsync(
            new ReplaceUserSegmentsForModeIntent(itemId, AnalysisMode.Commercial, [])));
        Assert.Empty(first.AffectedValues);

        await using (var db = CreateContext())
        {
            Assert.Empty(await db.AnalyzedItems.ToListAsync());
        }

        // With neither rows nor a record left, the same request is a true no-op.
        Assert.IsType<Ignored>(await service.ApplyAsync(
            new ReplaceUserSegmentsForModeIntent(itemId, AnalysisMode.Commercial, [])));
    }

    [Fact]
    public async Task DisabledMidApply_IsSkippedWithoutArmingBackoff()
    {
        var adapter = new RecordingProjectionAdapter { MirroringDisabledRemaining = 1 };
        var service = CreateService(adapter);
        var itemId = Guid.NewGuid();

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        // A toggle mid-apply is an outcome, not a failure: no backoff, no attempt
        // count, no failure text — the work stays immediately due for the enable
        // replay instead of waiting out a backoff the toggle never earned.
        Assert.Equal(ProjectionState.Skipped, outcome.Projection);
        await using (var db = CreateContext())
        {
            var queued = Assert.Single(await db.ProjectionQueue.ToListAsync());
            Assert.Equal(0, queued.AttemptCount);
            Assert.Null(queued.Failure);
            Assert.Null(queued.NextAttemptAt);
        }

        Assert.Equal(1, (await service.RetryProjectionAsync(ProjectionScope.ForItem(itemId))).RetriedCount);
        await using var verify = CreateContext();
        Assert.Empty(await verify.ProjectionQueue.ToListAsync());
    }

    [Fact]
    public async Task IgnoredIntent_StillConvergesMirror()
    {
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);
        var itemId = Guid.NewGuid();
        Assert.IsType<Accepted>(await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        // Re-asserting held state must re-project, so a diverged mirror (a ghost or
        // missing Jellyfin row) heals when the user retries instead of staying
        // unreachable behind the idempotence check.
        Assert.IsType<Ignored>(await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        Assert.Equal(2, adapter.Applies.Count);
        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
    }

    [Fact]
    public async Task NoReprojectProbe_DoesNotForceRunPendingWork()
    {
        var itemId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        adapter.FailingItems.Add(itemId);
        var service = CreateService(adapter);

        Assert.Equal(ProjectionState.Pending, Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20))).Projection);

        // A probe of an id that exists in no state journals nothing, and it must not
        // force-project either: the item's unrelated pending work keeps the backoff
        // its failure earned instead of retrying on every stray 404.
        var probe = Assert.IsType<Ignored>(await service.ApplyAsync(
            new DeleteSegmentIntent(itemId, Guid.NewGuid())));

        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, probe.Reason);
        Assert.Single(adapter.Attempts);
        await using var db = CreateContext();
        Assert.Equal(1, Assert.Single(await db.ProjectionQueue.ToListAsync()).AttemptCount);
    }

    [Fact]
    public async Task ExternalDelete_OfTombstonedSharedIdRow_LeavesOtherSegmentsAlone()
    {
        var itemId = Guid.NewGuid();
        var tombstone = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter)
        {
            State = SegmentState.Suppressed
        };
        var userRow = new DbSegment(itemId, AnalysisMode.Introduction, 50, 60, SegmentSource.User);
        await SeedAsync(tombstone, userRow);
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(tombstone.Id, itemId, MediaSegmentType.Intro, 10, 20)
        };
        var service = CreateService(adapter);

        var outcome = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, tombstone.Id, MediaSegmentType.Intro)));

        // The suppressed shared-id row means the plugin already treats the segment as
        // deleted: the delete is idempotently ignored, the journaled op only removes
        // the lingering ghost row, and no fallback may hard-delete the user's own
        // (sole active) segment of the mode.
        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, outcome.Reason);
        Assert.Empty(outcome.AffectedValues);
        Assert.Equal(tombstone.Id, Assert.Single(Assert.Single(adapter.Applies).ExternalOperations).ExternalSegmentId);
        await using var db = CreateContext();
        var rows = await db.Segments.OrderBy(row => row.StartTicks).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(SegmentState.Suppressed, rows[0].State);
        Assert.Equal(SegmentSource.User, rows[1].Source);
        Assert.Equal(SegmentState.Active, rows[1].State);
    }

    [Fact]
    public async Task ExternalResolutionInfrastructureFailure_PropagatesWithoutMutation()
    {
        await using (var db = CreateContext())
        {
            await db.ApplyMigrationsAsync();
        }

        var adapter = new RecordingProjectionAdapter { ResolveException = new InvalidOperationException("resolver unavailable") };
        var service = CreateService(adapter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new EditorDeleteSegmentIntent(Guid.NewGuid(), Guid.NewGuid(), MediaSegmentType.Intro)));

        await using var verify = CreateContext();
        Assert.Empty(await verify.Segments.ToListAsync());
        Assert.Empty(await verify.AnalyzedItems.ToListAsync());
        Assert.Empty(await verify.ProjectionQueue.ToListAsync());
        Assert.Empty(await verify.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task FailedApply_RetainsJournaledOperation()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20),
            FailuresRemaining = 1
        };
        var service = CreateService(adapter);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        Assert.Equal(ProjectionState.Pending, outcome.Projection);
        Assert.Equal(externalId, Assert.Single(Assert.Single(adapter.Attempts).ExternalOperations).ExternalSegmentId);
        await using var db = CreateContext();
        Assert.Equal(externalId, Assert.Single(await db.ProjectionExternalOperations.ToListAsync()).ExternalSegmentId);
        Assert.Equal(1, Assert.Single(await db.ProjectionQueue.ToListAsync()).AttemptCount);
    }

    [Fact]
    public async Task DisabledProjection_IsSkippedThenAppliedOnEnable()
    {
        var adapter = new RecordingProjectionAdapter();
        var policy = new FakeMirrorPolicy { Enabled = false };
        var service = CreateService(adapter, policy);
        var itemId = Guid.NewGuid();

        var accepted = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));
        Assert.Equal(ProjectionState.Skipped, accepted.Projection);
        Assert.Empty(adapter.Attempts);

        Assert.IsType<Accepted>(await service.ApplyAsync(new DeleteSegmentIntent(itemId, accepted.AffectedValues[0].Id)));
        await service.StartAsync(CancellationToken.None);
        policy.SetEnabled(true);
        var replayed = await adapter.Applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(itemId, replayed.ItemId);
        Assert.Empty(replayed.Segments);
        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
    }

    [Fact]
    public async Task SkippedExternalOperations_ReplayInOrderOnEnable()
    {
        var itemId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        adapter.ExternalTargets[firstId] = new ExternalSegmentTarget(firstId, itemId, MediaSegmentType.Intro, 10, 20);
        adapter.ExternalTargets[secondId] = new ExternalSegmentTarget(secondId, itemId, MediaSegmentType.Outro, 30, 40);
        var policy = new FakeMirrorPolicy { Enabled = false };
        var service = CreateService(adapter, policy);

        await service.ApplyAsync(new EditorDeleteSegmentIntent(itemId, firstId, MediaSegmentType.Intro));
        await service.ApplyAsync(new EditorDeleteSegmentIntent(itemId, secondId, MediaSegmentType.Outro));

        var skipped = Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items);
        Assert.Equal(ProjectionState.Skipped, skipped.State);

        await service.StartAsync(CancellationToken.None);
        policy.SetEnabled(true);
        var replay = await adapter.Applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { firstId, secondId }, replay.ExternalOperations.Select(operation => operation.ExternalSegmentId));
        Assert.Equal(ProjectionState.Applied, Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items).State);
        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task StartupRecovery_AppliesPendingWorkIgnoringBackoff()
    {
        var itemId = Guid.NewGuid();
        var failing = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        await CreateService(failing).ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Preview, 10, 20));
        var recovering = new RecordingProjectionAdapter();
        var service = CreateService(recovering);

        await service.StartAsync(CancellationToken.None);
        var applied = await recovering.Applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(itemId, applied.ItemId);
        Assert.Equal(ProjectionState.Applied, Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items).State);
    }

    [Fact]
    public async Task FailureForOneItem_DoesNotBlockAnotherItem()
    {
        var blockedItem = Guid.NewGuid();
        var progressingItem = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        adapter.FailingItems.Add(blockedItem);
        var service = CreateService(adapter);

        var blocked = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(blockedItem, AnalysisMode.Introduction, 10, 20)));
        var progressed = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(progressingItem, AnalysisMode.Credits, 30, 40)));

        Assert.Equal(ProjectionState.Pending, blocked.Projection);
        Assert.Equal(ProjectionState.Applied, progressed.Projection);
        Assert.Contains(adapter.Applies, applied => applied.ItemId == progressingItem);
    }

    [Fact]
    public async Task CompleteWork_AtStaleVersion_KeepsQueueRow()
    {
        var itemId = Guid.NewGuid();
        var database = CreateDatabase();
        ISegmentProjectionJournal journal = database;

        Assert.Null((await database.ApplyChangeAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20))).Outcome);
        var work = await journal.ReadProjectionWorkAsync(itemId, CancellationToken.None);
        Assert.NotNull(work);
        Assert.Null((await database.ApplyChangeAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40))).Outcome);

        // Completing at the projected (stale) version must not lose the newer work.
        await journal.CompleteProjectionWorkAsync(itemId, work.Item.Version, [], CancellationToken.None);
        var surviving = await journal.ReadProjectionWorkAsync(itemId, CancellationToken.None);
        Assert.NotNull(surviving);
        Assert.Equal(work.Item.Version + 1, surviving.Item.Version);

        await journal.CompleteProjectionWorkAsync(itemId, surviving.Item.Version, [], CancellationToken.None);
        Assert.Null(await journal.ReadProjectionWorkAsync(itemId, CancellationToken.None));
    }

    [Fact]
    public async Task Rebuild_RetainsPendingWorkAndOperations()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20),
            FailuresRemaining = 1
        };
        await CreateService(adapter).ApplyAsync(new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro));

        await using (var db = CreateContext())
        {
            await db.RebuildDatabaseAsync(CreateContext);
        }

        await using var verify = CreateContext();
        Assert.Single(await verify.ProjectionQueue.ToListAsync());
        Assert.Equal(externalId, Assert.Single(await verify.ProjectionExternalOperations.ToListAsync()).ExternalSegmentId);
    }

    /// <inheritdoc />
    public void Dispose() => DatabaseTestHelpers.DeleteSqliteFiles(_dbPath);

    private SegmentChange CreateService(RecordingProjectionAdapter adapter, FakeMirrorPolicy? policy = null)
    {
        var database = CreateDatabase();
        adapter.Database = database;
        return new SegmentChange(
            database,
            database,
            adapter,
            policy ?? new FakeMirrorPolicy(),
            new SegmentMutationLocks(),
            TimeProvider.System,
            NullLogger<SegmentChange>.Instance);
    }

    private IntroSkipperDatabase CreateDatabase()
    {
        var factory = new TestDbContextFactory<IntroSkipperDbContext>(() => new IntroSkipperDbContext(_dbPath));
        return new IntroSkipperDatabase(factory, NullLogger<IntroSkipperDatabase>.Instance);
    }

    private IntroSkipperDbContext CreateContext() => new(_dbPath);

    private async Task SeedAsync(params DbSegment[] segments)
    {
        await using var db = CreateContext();
        await db.ApplyMigrationsAsync();
        db.Segments.AddRange(segments);
        await db.SaveChangesAsync();
    }

    private async Task SeedAnalyzedItemAsync(params DbAnalyzedItem[] items)
    {
        await using var db = CreateContext();
        await db.ApplyMigrationsAsync();
        db.AnalyzedItems.AddRange(items);
        await db.SaveChangesAsync();
    }

    private async Task AssertAnalyzedItemAsync(Guid itemId, AnalysisMode mode, string expectedHash)
    {
        await using var db = CreateContext();
        var item = await db.AnalyzedItems.SingleAsync(value => value.ItemId == itemId && value.Type == mode);
        Assert.Equal(expectedHash, item.ConfigHash);
    }

    /// <summary>One recorded adapter apply: the item's servable image at apply time plus the journaled operations.</summary>
    private sealed record AppliedProjection(Guid ItemId, IReadOnlyList<DbSegment> Segments, IReadOnlyList<ProjectedExternalOperation> ExternalOperations);

    private sealed class RecordingProjectionAdapter : ISegmentProjectionAdapter
    {
        public IIntroSkipperDatabase? Database { get; set; }

        public int FailuresRemaining { get; set; }

        public ExternalSegmentTarget? ExternalTarget { get; set; }

        public Exception? ResolveException { get; set; }

        public Dictionary<Guid, ExternalSegmentTarget> ExternalTargets { get; } = [];

        public HashSet<Guid> FailingItems { get; } = [];

        public TaskCompletionSource<AppliedProjection> Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AppliedProjection> Attempts { get; } = [];

        public List<AppliedProjection> Applies { get; } = [];

        public int MirroringDisabledRemaining { get; set; }

        public Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken)
        {
            if (ResolveException is not null)
            {
                throw ResolveException;
            }

            return Task.FromResult(ExternalTargets.TryGetValue(externalSegmentId, out var target) ? target : ExternalTarget);
        }

        public async Task<ProjectionApplyOutcome> ApplyAsync(Guid itemId, IReadOnlyList<ProjectedExternalOperation> externalOperations, CancellationToken cancellationToken)
        {
            // Snapshot what the real adapter would push: the item's current servable
            // image (the disable filter applied), read through the facade.
            IReadOnlyList<DbSegment> image = Database is null
                ? []
                : await Database.GetServableSegmentsAsync(itemId, cancellationToken);
            var snapshot = new AppliedProjection(itemId, image, [.. externalOperations]);
            Attempts.Add(snapshot);
            if (MirroringDisabledRemaining-- > 0)
            {
                return ProjectionApplyOutcome.MirroringDisabled;
            }

            if (FailuresRemaining-- > 0 || FailingItems.Contains(itemId))
            {
                throw new InvalidOperationException("synthetic projection failure");
            }

            Applies.Add(snapshot);
            Applied.TrySetResult(snapshot);
            return ProjectionApplyOutcome.Applied;
        }
    }
}
