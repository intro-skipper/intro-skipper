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

    /// <summary>What the resolver reports for an uncorrelated editor delete.</summary>
    public enum ResolvedRow
    {
        None,
        OtherItem,
        OtherType,
        OtherId,
    }

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
        await AssertQueueEmptyAsync();
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

        Assert.Equal(1, await service.ProjectItemsAsync([itemId]));
        Assert.Equal(2, adapter.Attempts.Count);
        await AssertQueueEmptyAsync();
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

        // One retry applies the item's current truth: both segments in one pass.
        Assert.Equal(1, await service.ProjectItemsAsync([itemId]));
        Assert.Equal(2, Assert.Single(adapter.Applies).Segments.Count);
        await AssertQueueEmptyAsync();
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
        // twin's targeted delete is journaled with the row's own shape, the durable
        // record that lets a retry answer idempotently while the sync is pending.
        Assert.Equal(ProjectionState.Applied, outcome.Projection);
        Assert.Equal(row.Id, Assert.Single(outcome.AffectedValues).Id);
        var operation = Assert.Single(Assert.Single(adapter.Applies).ExternalOperations);
        Assert.Equal(row.Id, operation.ExternalSegmentId);
        Assert.Equal(10, operation.StartTicks);
        await AssertSegmentsAsync();
        await AssertQueueEmptyAsync();
    }

    // Two active user intros. The first delete hard-deletes intro A and leaves its
    // Jellyfin row's targeted delete journaled (the immediate projection fails), so the
    // editor still lists the mirrored twin. A repeated delete of that row (a
    // double-click, a retry, a plural-API delete retried through the editor, a request
    // that resolved before the first one's projection ran) must answer idempotently
    // from the journal: without it, the mode-wide fallback would claim the surviving
    // intro the user never addressed.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RetriedDelete_IsIgnored_AndClaimsNoFurtherSegment(bool firstViaEditor, bool rowSharesPluginId)
    {
        var itemId = Guid.NewGuid();
        var claimed = new DbSegment(itemId, AnalysisMode.Introduction, 1000, 2000, SegmentSource.User);
        var survivor = new DbSegment(itemId, AnalysisMode.Introduction, 5000, 6000, SegmentSource.User);
        await SeedAsync(claimed, survivor);
        var rowId = rowSharesPluginId ? claimed.Id : Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        adapter.ExternalTargets[rowId] = new ExternalSegmentTarget(rowId, itemId, MediaSegmentType.Intro, 1000, 2000);
        var service = CreateService(adapter);
        SegmentChangeIntent first = firstViaEditor
            ? new EditorDeleteSegmentIntent(itemId, rowId, MediaSegmentType.Intro)
            : new DeleteSegmentIntent(itemId, claimed.Id);

        Assert.Equal(ProjectionState.Pending, Assert.IsType<Accepted>(await service.ApplyAsync(first)).Projection);
        var second = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, rowId, MediaSegmentType.Intro)));

        // The retry's re-projection then converged: the journaled delete ran exactly
        // once, and the surviving intro was never touched.
        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, second.Reason);
        Assert.Equal(rowId, Assert.Single(Assert.Single(adapter.Applies).ExternalOperations).ExternalSegmentId);
        await AssertSegmentsAsync(row => Assert.Equal(survivor.Id, row.Id));
        await AssertQueueEmptyAsync();
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
        await AssertQueueEmptyAsync();
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
        // fallback's drift heal must still work: the retry hazards it once posed
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
        Assert.Equal(SegmentState.Suppressed, Assert.Single(await SegmentsAsync(), s => s.Id == drifted.Id).State);
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
        await AssertQueueEmptyAsync();
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
        await AssertSegmentsAsync();
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
        await AssertSegmentsAsync(row => Assert.Equal(SegmentState.Active, row.State));
        await AssertQueueEmptyAsync();
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
        await AssertQueueEmptyAsync();
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
        await AssertSegmentsAsync();
        await AssertQueueEmptyAsync();
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

        await AssertSegmentsAsync(row => Assert.Equal(shiftedCopy.Id, row.Id));
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
        await AssertQueueEmptyAsync();
    }

    // No plugin row owns the id, and what the Jellyfin resolution reports does not
    // corroborate the request: no row at all, a row of another item, a row of another
    // type, or a row under a different id than requested (the facade is a public API
    // and must not trust the resolver's pairing). Every case rejects before commit.
    [Theory]
    [InlineData(ResolvedRow.None, SegmentChangeRejectedReason.ExternalSegmentNotFound)]
    [InlineData(ResolvedRow.OtherItem, SegmentChangeRejectedReason.ExternalItemMismatch)]
    [InlineData(ResolvedRow.OtherType, SegmentChangeRejectedReason.ExternalTypeMismatch)]
    [InlineData(ResolvedRow.OtherId, SegmentChangeRejectedReason.ExternalSegmentNotFound)]
    public async Task EditorDelete_UncorrelatedResolutionMismatch_RejectsBeforeCommit(ResolvedRow resolved, SegmentChangeRejectedReason expected)
    {
        var itemId = Guid.NewGuid();
        var requestedId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        if (resolved != ResolvedRow.None)
        {
            adapter.ExternalTargets[requestedId] = new ExternalSegmentTarget(
                resolved == ResolvedRow.OtherId ? Guid.NewGuid() : requestedId,
                resolved == ResolvedRow.OtherItem ? Guid.NewGuid() : itemId,
                resolved == ResolvedRow.OtherType ? MediaSegmentType.Outro : MediaSegmentType.Intro,
                10,
                20);
        }

        var service = CreateService(adapter);

        var rejected = Assert.IsType<Rejected>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, requestedId, MediaSegmentType.Intro)));

        Assert.Equal(expected, rejected.Reason);
        await AssertQueueEmptyAsync();
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
        await AssertSegmentsAsync(
            row => Assert.Equal(SegmentSource.User, row.Source),
            row => Assert.Equal(SegmentSource.User, row.Source));
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
        await AssertSegmentsAsync(row => Assert.Equal(Assert.Single(first.AffectedValues).Id, row.Id));
        await AssertQueueEmptyAsync();
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
        await AssertSegmentsAsync(row =>
        {
            Assert.Equal(SegmentSource.User, row.Source);
            Assert.Equal(string.Empty, row.ConfigHash);
        });
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
        await AssertSegmentsAsync(
            row =>
            {
                Assert.Equal(automatic.Id, row.Id);
                Assert.Equal(SegmentSource.User, row.Source);
            },
            row => Assert.Equal(SegmentSource.User, row.Source));
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
        await AssertSegmentsAsync(remaining =>
        {
            Assert.Equal(SegmentState.Suppressed, remaining.State);
            Assert.Equal(SegmentSource.Chapter, remaining.Source);
        });
        await using var db = CreateContext();
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
        await AssertSegmentsAsync(row =>
        {
            Assert.Equal(occupant.Id, row.Id);
            Assert.Equal(SegmentSource.User, row.Source);
        });
    }

    [Fact]
    public async Task Delete_ClearsAnalysisRecordAndTombstonesAutomaticRow()
    {
        var userItemId = Guid.NewGuid();
        var deletedUser = new DbSegment(userItemId, AnalysisMode.Commercial, 10, 20, SegmentSource.User);
        await SeedAsync(deletedUser);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(userItemId, AnalysisMode.Commercial, "user-mode"));
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);

        await service.ApplyAsync(new DeleteSegmentIntent(userItemId, deletedUser.Id));

        var autoItemId = Guid.NewGuid();
        var deletedAuto = new DbSegment(autoItemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter, "auto-hash");
        await SeedAsync(deletedAuto);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(autoItemId, AnalysisMode.Introduction, "auto-hash"));

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(new DeleteSegmentIntent(autoItemId, deletedAuto.Id)));

        // The user row is gone, the automatic row is a tombstone, and both items
        // projected an empty image.
        Assert.Equal(SegmentState.Suppressed, Assert.Single(outcome.AffectedValues).State);
        Assert.Equal(2, adapter.Applies.Count);
        Assert.All(adapter.Applies, applied => Assert.Empty(applied.Segments));
        await AssertSegmentsAsync(row =>
        {
            Assert.Equal(autoItemId, row.ItemId);
            Assert.Equal(SegmentState.Suppressed, row.State);
        });
        await using var db = CreateContext();
        Assert.Empty(await db.AnalyzedItems.ToListAsync());
    }

    // Restoring re-arms the analysis record from the row's hash unless a newer record
    // already exists, and drops the row's own hash either way: the hash-driven stale
    // cleanup only judges rows carrying one, so a restored row that kept its hash would
    // be deleted again on the next configuration change.
    [Theory]
    [InlineData(null, "restore-hash")]
    [InlineData("newer", "newer")]
    public async Task RestoreAutomatic_RearmsAnalyzedRecordAndDropsRowHash(string? existingRecordHash, string expectedRecordHash)
    {
        var itemId = Guid.NewGuid();
        var tombstone = new DbSegment(itemId, AnalysisMode.Credits, 10, 20, SegmentSource.BlackFrame, "restore-hash")
        {
            State = SegmentState.Suppressed
        };
        await SeedAsync(tombstone);
        if (existingRecordHash is not null)
        {
            await SeedAnalyzedItemAsync(new DbAnalyzedItem(itemId, AnalysisMode.Credits, existingRecordHash));
        }

        Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new RestoreSegmentIntent(itemId, tombstone.Id)));

        await AssertAnalyzedItemAsync(itemId, AnalysisMode.Credits, expectedRecordHash);
        await AssertSegmentsAsync(row =>
        {
            Assert.Equal(SegmentState.Active, row.State);
            Assert.Equal(string.Empty, row.ConfigHash);
        });
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
        var adapter = new RecordingProjectionAdapter();
        adapter.ExternalTargets[externalId] = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20);
        var service = CreateService(adapter);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        Assert.Equal(counterpart.Id, Assert.Single(outcome.AffectedValues).Id);
        await AssertSegmentsAsync(row => Assert.Equal(SegmentState.Suppressed, row.State));
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
        // count, no failure text. The work stays immediately due for the enable
        // replay instead of waiting out a backoff the toggle never earned.
        Assert.Equal(ProjectionState.Skipped, outcome.Projection);
        await using (var db = CreateContext())
        {
            var queued = Assert.Single(await db.ProjectionQueue.ToListAsync());
            Assert.Equal(0, queued.AttemptCount);
            Assert.Null(queued.Failure);
            Assert.Null(queued.NextAttemptAt);
        }

        Assert.Equal(1, await service.ProjectItemsAsync([itemId]));
        await AssertQueueEmptyAsync();
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
        await AssertQueueEmptyAsync();
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
        var adapter = new RecordingProjectionAdapter();
        adapter.ExternalTargets[tombstone.Id] = new ExternalSegmentTarget(tombstone.Id, itemId, MediaSegmentType.Intro, 10, 20);
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
        await AssertSegmentsAsync(
            row => Assert.Equal(SegmentState.Suppressed, row.State),
            row =>
            {
                Assert.Equal(SegmentSource.User, row.Source);
                Assert.Equal(SegmentState.Active, row.State);
            });
    }

    [Fact]
    public async Task ExternalResolutionInfrastructureFailure_PropagatesWithoutMutation()
    {
        var adapter = new RecordingProjectionAdapter { ResolveException = new InvalidOperationException("resolver unavailable") };
        var service = CreateService(adapter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new EditorDeleteSegmentIntent(Guid.NewGuid(), Guid.NewGuid(), MediaSegmentType.Intro)));

        await AssertSegmentsAsync();
        await AssertQueueEmptyAsync();
        await using var verify = CreateContext();
        Assert.Empty(await verify.AnalyzedItems.ToListAsync());
    }

    [Fact]
    public async Task FailedApply_RetainsJournaledOperation()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        adapter.ExternalTargets[externalId] = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20);
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
        await AssertQueueEmptyAsync();
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

        // Skipped while disabled: the work sits journaled without any backoff.
        await using (var db = CreateContext())
        {
            var queued = Assert.Single(await db.ProjectionQueue.ToListAsync());
            Assert.Equal(0, queued.AttemptCount);
            Assert.Equal(2, (await db.ProjectionExternalOperations.ToListAsync()).Count);
        }

        await service.StartAsync(CancellationToken.None);
        policy.SetEnabled(true);
        var replay = await adapter.Applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { firstId, secondId }, replay.ExternalOperations.Select(operation => operation.ExternalSegmentId));
        await AssertQueueEmptyAsync();
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
        await AssertQueueEmptyAsync();
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
        var (journal, itemId, staleVersion) = await SeedSupersededWorkAsync();

        // Completing at the projected (stale) version must not lose the newer work,
        // and must report the miss so callers do not claim the item converged.
        Assert.False(await journal.CompleteProjectionWorkAsync(itemId, staleVersion, [], CancellationToken.None));
        var surviving = await journal.ReadProjectionWorkAsync(itemId, CancellationToken.None);
        Assert.NotNull(surviving);
        Assert.Equal(staleVersion + 1, surviving.Item.Version);

        Assert.True(await journal.CompleteProjectionWorkAsync(itemId, surviving.Item.Version, [], CancellationToken.None));
        Assert.Null(await journal.ReadProjectionWorkAsync(itemId, CancellationToken.None));
    }

    [Fact]
    public async Task RecordFailure_AtStaleVersion_DoesNotArmBackoff()
    {
        var (journal, itemId, staleVersion) = await SeedSupersededWorkAsync();

        // A failure recorded at the projected (stale) version must not push the
        // newer work, enqueued due immediately, behind that failure's backoff.
        await journal.RecordProjectionFailureAsync(itemId, staleVersion, DateTime.UtcNow.AddMinutes(5), "stale failure", CancellationToken.None);
        var current = await journal.ReadProjectionWorkAsync(itemId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Null(current.Item.NextAttemptAt);
        Assert.Equal(0, current.Item.AttemptCount);
    }

    [Fact]
    public async Task CompletionSupersededMidApply_ReportsPendingNotApplied()
    {
        var itemId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);

        // An analyzer write holds no projection stripe, so it can land while a
        // projection is mid-apply: the marker's version bumps and the completion
        // must miss it, and the outcome must say Pending, not Applied, so retry
        // counts and the HTTP 202 mapping agree with the surviving marker.
        adapter.OnApply = () => adapter.Database!.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(30, 40))], SegmentSource.Chapter);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        Assert.Equal(ProjectionState.Pending, outcome.Projection);
        await using (var db = CreateContext())
        {
            Assert.Equal(2, Assert.Single(await db.ProjectionQueue.ToListAsync()).Version);
        }

        // With no further interleaving the surviving work converges on retry.
        adapter.OnApply = null;
        Assert.Equal(1, await service.ProjectItemsAsync([itemId]));
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task Rebuild_RetainsPendingWorkAndOperations()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        adapter.ExternalTargets[externalId] = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20);
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
        await db.Database.MigrateAsync();
        db.Segments.AddRange(segments);
        await db.SaveChangesAsync();
    }

    private async Task SeedAnalyzedItemAsync(params DbAnalyzedItem[] items)
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        db.AnalyzedItems.AddRange(items);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Journals two changes for one item and returns the first marker's version, which
    /// the second change superseded.
    /// </summary>
    private async Task<(ISegmentProjectionJournal Journal, Guid ItemId, long StaleVersion)> SeedSupersededWorkAsync()
    {
        var itemId = Guid.NewGuid();
        var database = CreateDatabase();
        ISegmentProjectionJournal journal = database;

        Assert.Null((await database.ApplyChangeAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20))).Outcome);
        var work = await journal.ReadProjectionWorkAsync(itemId, CancellationToken.None);
        Assert.NotNull(work);
        Assert.Null((await database.ApplyChangeAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40))).Outcome);
        return (journal, itemId, work.Item.Version);
    }

    private async Task AssertAnalyzedItemAsync(Guid itemId, AnalysisMode mode, string expectedHash)
    {
        await using var db = CreateContext();
        var item = await db.AnalyzedItems.SingleAsync(value => value.ItemId == itemId && value.Type == mode);
        Assert.Equal(expectedHash, item.ConfigHash);
    }

    /// <summary>Asserts that no projection work of any kind is journaled.</summary>
    private async Task AssertQueueEmptyAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
    }

    private async Task<List<DbSegment>> SegmentsAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        return await db.Segments.OrderBy(row => row.StartTicks).ToListAsync();
    }

    /// <summary>Asserts the stored rows, ordered by start ticks, one inspector per expected row.</summary>
    private async Task AssertSegmentsAsync(params Action<DbSegment>[] rowInspectors)
        => Assert.Collection(await SegmentsAsync(), rowInspectors);

    /// <summary>One recorded adapter apply: the item's servable image at apply time plus the journaled operations.</summary>
    private sealed record AppliedProjection(Guid ItemId, IReadOnlyList<DbSegment> Segments, IReadOnlyList<DbProjectionExternalOperation> ExternalOperations);

    private sealed class RecordingProjectionAdapter : ISegmentProjectionAdapter
    {
        public IIntroSkipperDatabase? Database { get; set; }

        public int FailuresRemaining { get; set; }

        public Exception? ResolveException { get; set; }

        /// <summary>Gets the Jellyfin rows the resolver serves, keyed by the requested segment id.</summary>
        public Dictionary<Guid, ExternalSegmentTarget> ExternalTargets { get; } = [];

        public HashSet<Guid> FailingItems { get; } = [];

        public TaskCompletionSource<AppliedProjection> Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AppliedProjection> Attempts { get; } = [];

        public List<AppliedProjection> Applies { get; } = [];

        public int MirroringDisabledRemaining { get; set; }

        // Runs mid-apply, after the snapshot but before the outcome: the seam for
        // simulating an unstriped facade write landing while a projection is in flight.
        public Func<Task>? OnApply { get; set; }

        public Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken)
        {
            if (ResolveException is not null)
            {
                throw ResolveException;
            }

            return Task.FromResult(ExternalTargets.GetValueOrDefault(externalSegmentId));
        }

        public async Task<ProjectionApplyOutcome> ApplyAsync(Guid itemId, IReadOnlyList<DbProjectionExternalOperation> externalOperations, CancellationToken cancellationToken)
        {
            // Snapshot what the real adapter would push: the item's current servable
            // image (the disable filter applied), read through the facade.
            IReadOnlyList<DbSegment> image = Database is null
                ? []
                : await Database.GetServableSegmentsAsync(itemId, cancellationToken);
            var snapshot = new AppliedProjection(itemId, image, [.. externalOperations]);
            Attempts.Add(snapshot);
            if (OnApply is { } onApply)
            {
                await onApply();
            }

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
