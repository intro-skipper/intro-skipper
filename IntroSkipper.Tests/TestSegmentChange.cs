// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Durability and semantics tests for the segment change coordinator, composed over the
/// real mirror and a <see cref="FakeJellyfinSegmentStore"/>, so every assertion about
/// projection is about what Jellyfin ends up holding.
/// </summary>
public sealed class TestSegmentChange : IDisposable
{
    private readonly TempSegmentDb _db = new();

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
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var itemId = Guid.NewGuid();

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        Assert.Equal(ProjectionState.Applied, outcome.Projection);
        var affected = Assert.Single(outcome.AffectedValues);
        Assert.Equal(SegmentSource.User, affected.Source);
        var (pushedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, pushedItemId);
        Assert.Equal(affected.Id, Assert.Single(pushed).Id);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task ProjectionFailure_DoesNotRollBackAuthoritativeMutation_AndManualRetryConverges()
    {
        var store = new FakeJellyfinSegmentStore { WriteException = JellyfinDown() };
        var service = CreateService(store);
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

        store.WriteException = null;
        Assert.Equal(1, await service.ProjectItemsAsync([itemId]));
        Assert.Equal(2, store.WriteCallCount);
        Assert.Single(await MirroredAsync(store, itemId));
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task CoalescedWork_AppliesLatestTruthOnce()
    {
        var store = new FakeJellyfinSegmentStore { WriteException = JellyfinDown() };
        var service = CreateService(store);
        var itemId = Guid.NewGuid();

        await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20));
        await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40));

        // Both immediate attempts failed; the work coalesced into one marker.
        Assert.Equal(2, store.WriteCallCount);
        await using (var db = CreateContext())
        {
            Assert.Single(await db.ProjectionQueue.ToListAsync());
        }

        // One retry applies the item's current truth: both segments in one write.
        store.WriteException = null;
        Assert.Equal(1, await service.ProjectItemsAsync([itemId]));
        Assert.Equal(3, store.WriteCallCount);
        Assert.Equal(2, (await MirroredAsync(store, itemId)).Count);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task EditorDelete_CorrelatedActiveRow_JournalsItsTwinRowsTargetedDelete()
    {
        var itemId = Guid.NewGuid();
        var row = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.User);
        await SeedAsync(row);
        var store = new FakeJellyfinSegmentStore { ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, row.Id, MediaSegmentType.Intro, 10, 20)] };
        var service = CreateService(store);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, row.Id, MediaSegmentType.Intro)));

        // The plugin row owns the id: the delete is authoritative, and its Jellyfin
        // twin's targeted delete is journaled with the row's own shape, the durable
        // record that lets a retry answer idempotently while the sync is pending. The
        // validated delete removed the twin, so the sync behind it had nothing to write.
        Assert.Equal(ProjectionState.Applied, outcome.Projection);
        Assert.Equal(row.Id, Assert.Single(outcome.AffectedValues).Id);
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);
        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(await MirroredAsync(store, itemId));
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
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, rowId, MediaSegmentType.Intro, 1000, 2000)],
            DeleteSegmentException = JellyfinDown(),
        };
        var service = CreateService(store);
        SegmentChangeIntent first = firstViaEditor
            ? new EditorDeleteSegmentIntent(itemId, rowId, MediaSegmentType.Intro)
            : new DeleteSegmentIntent(itemId, claimed.Id);

        Assert.Equal(ProjectionState.Pending, Assert.IsType<Accepted>(await service.ApplyAsync(first)).Projection);
        store.DeleteSegmentException = null;
        var second = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, rowId, MediaSegmentType.Intro)));

        // The retry's re-projection then converged: the journaled delete ran exactly
        // once, and the surviving intro was never touched.
        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, second.Reason);
        Assert.Equal([(itemId, rowId)], store.DeletedSegments);
        Assert.Equal(survivor.Id, Assert.Single(await MirroredAsync(store, itemId)).Id);
        await AssertSegmentsAsync(row => Assert.Equal(survivor.Id, row.Id));
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task EditorDelete_RewrittenExternalRow_JournalsAFreshOperation()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore { DeleteSegmentException = JellyfinDown() };
        AddForeign(store, itemId, externalId, MediaSegmentType.Intro, 10, 20);
        var service = CreateService(store);

        Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        // The foreign provider rewrote the row under its stable id while the first
        // delete's projection was pending: the re-delete must journal a fresh
        // operation for the new shape instead of reporting the old one as covering
        // it (the old operation drops harmlessly as superseded at apply time).
        AddForeign(store, itemId, externalId, MediaSegmentType.Intro, 10, 25);
        store.DeleteSegmentException = null;
        Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        Assert.Equal([(itemId, externalId)], store.DeletedSegments);
        Assert.Empty(store.ForeignSegments);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task ModeWideFallback_HealsDrift_EvenWithUnrelatedPendingWork()
    {
        var itemId = Guid.NewGuid();
        var drifted = new DbSegment(itemId, AnalysisMode.Introduction, 5000, 6000, SegmentSource.Chapter);
        await SeedAsync(drifted);
        var store = new FakeJellyfinSegmentStore { WriteException = JellyfinDown() };
        var service = CreateService(store);

        // An unrelated failed change leaves the item's marker pending. The
        // fallback's drift heal must still work: the retry hazards it once posed
        // are answered by the pending-op guard (every single-row delete journals
        // its target's operation), not by suppressing the heal.
        Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40)));

        var externalId = Guid.NewGuid();
        AddForeign(store, itemId, externalId, MediaSegmentType.Intro, 100, 200);
        store.WriteException = null;
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
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);

        // Ids that exist in no state have nothing to heal: the 404-style probes
        // must not pay a journal write and a mirror sync. Any sync would write here,
        // since Jellyfin does not hold the seeded row yet.
        Assert.IsType<Ignored>(await service.ApplyAsync(new DeleteSegmentIntent(itemId, Guid.NewGuid())));
        Assert.IsType<Ignored>(await service.ApplyAsync(new RestoreSegmentIntent(itemId, Guid.NewGuid())));

        Assert.Equal(0, store.WriteCallCount);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task EditorDelete_CorrelatedRow_NeedsNoJellyfinResolution()
    {
        var itemId = Guid.NewGuid();
        var row = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.User);
        await SeedAsync(row);
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, row.Id, MediaSegmentType.Intro, 10, 20)],
            FindSegmentException = JellyfinDown(),
        };
        var service = CreateService(store);

        // A plugin row owns the id, so the dispatch is decided authoritatively: the
        // failing Jellyfin lookup is never consulted, neither to resolve the delete nor
        // during its projection, which removes the twin by its validated shape.
        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, row.Id, MediaSegmentType.Intro)));

        Assert.Equal(ProjectionState.Applied, outcome.Projection);
        Assert.Equal(row.Id, Assert.Single(outcome.AffectedValues).Id);
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);
        await AssertSegmentsAsync();
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task EditorDelete_CorrelatedTypeMismatch_RejectsWithActualType()
    {
        var itemId = Guid.NewGuid();
        var credits = new DbSegment(itemId, AnalysisMode.Credits, 30, 40, SegmentSource.Chapter);
        await SeedAsync(credits);
        var service = CreateService(new FakeJellyfinSegmentStore());

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
        var store = new FakeJellyfinSegmentStore { ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, tombstone.Id, MediaSegmentType.Intro, 10, 20)] };
        var service = CreateService(store);

        var ignored = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, tombstone.Id, MediaSegmentType.Intro)));

        // The plugin already treats the row as deleted; the journaled re-projection
        // and the tombstone's targeted delete are what remove a ghost Jellyfin row
        // re-added since.
        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, ignored.Reason);
        Assert.Equal([(itemId, tombstone.Id)], store.DeletedSegments);
        Assert.Empty(await MirroredAsync(store, itemId));
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
        var store = new FakeJellyfinSegmentStore();
        AddForeign(store, itemId, jellyfinRowId, MediaSegmentType.Intro, 1238397, 500000000);
        var service = CreateService(store);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, jellyfinRowId, MediaSegmentType.Intro)));

        // The one-tick-off counterpart is deleted so the very sync this change
        // journals cannot resurrect the segment, and the foreign row's delete is
        // journaled with its validated boundaries, which the delete predicate matched.
        Assert.Equal(ProjectionState.Applied, outcome.Projection);
        Assert.Equal([(itemId, jellyfinRowId)], store.DeletedSegments);
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
        var store = new FakeJellyfinSegmentStore();
        AddForeign(store, itemId, jellyfinRowId, MediaSegmentType.Intro, 1238398, 500000000);
        var service = CreateService(store);

        Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, jellyfinRowId, MediaSegmentType.Intro)));

        await AssertSegmentsAsync(row => Assert.Equal(shiftedCopy.Id, row.Id));
    }

    [Fact]
    public async Task EditorDelete_RetryAfterJournaledDeleteEmptiedJellyfin_IsIgnoredNotRejected()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();

        // A plugin row in an unrelated mode gives the item sync something to write, so
        // the failing write lands after the journaled delete already ran.
        await SeedAsync(new DbSegment(itemId, AnalysisMode.Recap, 500, 600, SegmentSource.User));
        var store = new FakeJellyfinSegmentStore { WriteException = JellyfinDown() };
        AddForeign(store, itemId, externalId, MediaSegmentType.Intro, 10, 20);
        var service = CreateService(store);

        var first = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        // The journaled operation deletes the Jellyfin row before the item sync, so
        // the failed sync left the row gone while the work is still pending.
        Assert.Equal(ProjectionState.Pending, first.Projection);
        Assert.Empty(store.ForeignSegments);

        // A probe claiming another type earns no idempotent answer: nothing
        // resolvable corroborates it, and the pending operation records a different
        // delete.
        var otherType = Assert.IsType<Rejected>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Outro)));
        Assert.Equal(SegmentChangeRejectedReason.ExternalSegmentNotFound, otherType.Reason);

        // The true retry answers idempotently from the journal instead of 404ing,
        // and its re-projection converges the pending work.
        store.WriteException = null;
        var retry = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, retry.Reason);
        Assert.Equal([(itemId, externalId)], store.DeletedSegments);
        Assert.Single(await MirroredAsync(store, itemId));
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
        var target = resolved == ResolvedRow.None
            ? null
            : SegmentChangeHarness.MirroredDto(
                resolved == ResolvedRow.OtherItem ? Guid.NewGuid() : itemId,
                resolved == ResolvedRow.OtherId ? Guid.NewGuid() : requestedId,
                resolved == ResolvedRow.OtherType ? MediaSegmentType.Outro : MediaSegmentType.Intro,
                10,
                20);

        var result = await CreateDatabase().ApplyChangeAsync(
            new EditorDeleteSegmentIntent(itemId, requestedId, MediaSegmentType.Intro),
            () => Task.FromResult(target));

        Assert.Equal(expected, Assert.IsType<Rejected>(result.Outcome).Reason);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task IgnoredIdempotentAddAndUpdate_ReportTheExistingRow()
    {
        var itemId = Guid.NewGuid();
        var service = CreateService(new FakeJellyfinSegmentStore());
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
        var userRow = new DbSegment(itemId, AnalysisMode.Credits, 30, 40, SegmentSource.User);
        await SeedAsync(
            new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter),
            userRow);
        var store = new FakeJellyfinSegmentStore { WriteException = JellyfinDown() };
        var service = CreateService(store);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new SegmentVisibilityChangeIntent(itemId, Visible: false)));

        Assert.Equal(ProjectionState.Pending, outcome.Projection);
        var (_, attempted) = Assert.Single(store.ReplacedItems);
        Assert.Equal(userRow.Id, Assert.Single(attempted).Id);
        await using var db = CreateContext();
        Assert.True(await db.DisabledItems.AnyAsync(item => item.ItemId == itemId));
    }

    [Fact]
    public async Task IdenticalReplaceRewrite_IsIgnoredAndKeepsIds()
    {
        var itemId = Guid.NewGuid();
        var service = CreateService(new FakeJellyfinSegmentStore());
        var first = Assert.IsType<Accepted>(await service.ApplyAsync(new ReplaceUserSegmentsForModeIntent(
            itemId, AnalysisMode.Introduction, [new SegmentRange(10, 20)])));

        var second = Assert.IsType<Ignored>(await service.ApplyAsync(new ReplaceUserSegmentsForModeIntent(
            itemId, AnalysisMode.Introduction, [new SegmentRange(10, 20)])));

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

        var accepted = Assert.IsType<Accepted>(await CreateService(new FakeJellyfinSegmentStore()).ApplyAsync(
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

        var accepted = Assert.IsType<Accepted>(await CreateService(new FakeJellyfinSegmentStore()).ApplyAsync(
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
        var userRow = new DbSegment(itemId, AnalysisMode.Commercial, 10, 20, SegmentSource.User);
        var autoRow = new DbSegment(itemId, AnalysisMode.Commercial, 30, 40, SegmentSource.Chapter, "auto-hash");
        await SeedAsync(userRow, autoRow);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(itemId, AnalysisMode.Commercial, "auto-hash"));
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                SegmentChangeHarness.MirroredDto(itemId, userRow.Id, MediaSegmentType.Commercial, 10, 20),
                SegmentChangeHarness.MirroredDto(itemId, autoRow.Id, MediaSegmentType.Commercial, 30, 40),
            ],
        };

        Assert.IsType<Accepted>(await CreateService(store).ApplyAsync(
            new ReplaceUserSegmentsForModeIntent(itemId, AnalysisMode.Commercial, [])));

        Assert.Empty(await MirroredAsync(store, itemId));
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

        var accepted = Assert.IsType<Accepted>(await CreateService(new FakeJellyfinSegmentStore()).ApplyAsync(
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
        var autoItemId = Guid.NewGuid();
        var deletedAuto = new DbSegment(autoItemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter, "auto-hash");
        await SeedAsync(deletedUser);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(userItemId, AnalysisMode.Commercial, "user-mode"));
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                SegmentChangeHarness.MirroredDto(userItemId, deletedUser.Id, MediaSegmentType.Commercial, 10, 20),
                SegmentChangeHarness.MirroredDto(autoItemId, deletedAuto.Id, MediaSegmentType.Intro, 10, 20),
            ],
        };
        var service = CreateService(store);

        await service.ApplyAsync(new DeleteSegmentIntent(userItemId, deletedUser.Id));

        await SeedAsync(deletedAuto);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(autoItemId, AnalysisMode.Introduction, "auto-hash"));

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(new DeleteSegmentIntent(autoItemId, deletedAuto.Id)));

        // The user row is gone, the automatic row is a tombstone, and Jellyfin holds
        // neither item's row any more.
        Assert.Equal(SegmentState.Suppressed, Assert.Single(outcome.AffectedValues).State);
        Assert.Empty(await MirroredAsync(store, userItemId));
        Assert.Empty(await MirroredAsync(store, autoItemId));
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

        Assert.IsType<Accepted>(await CreateService(new FakeJellyfinSegmentStore()).ApplyAsync(
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
        var store = new FakeJellyfinSegmentStore();
        AddForeign(store, itemId, externalId, MediaSegmentType.Intro, 10, 20);
        var service = CreateService(store);

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
        var service = CreateService(new FakeJellyfinSegmentStore());

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
    public async Task DisabledMirror_IsSkippedWithoutArmingBackoff()
    {
        var store = new FakeJellyfinSegmentStore();
        var policy = new FakeMirrorPolicy { Enabled = false };
        var service = CreateService(store, policy);
        var itemId = Guid.NewGuid();

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        // A disabled mirror is an outcome, not a failure: no backoff, no attempt
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

        policy.Enabled = true;
        Assert.Equal(1, await service.ProjectItemsAsync([itemId]));
        Assert.Single(await MirroredAsync(store, itemId));
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task IgnoredIntent_StillConvergesMirror()
    {
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var itemId = Guid.NewGuid();
        Assert.IsType<Accepted>(await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        // Jellyfin loses the row behind the plugin's back.
        var mirrored = Assert.Single(await MirroredAsync(store, itemId));
        await store.DeleteValidatedSegmentAsync(itemId, mirrored.Id, mirrored.Type, mirrored.StartTicks, mirrored.EndTicks, CancellationToken.None);

        // Re-asserting held state must re-project, so a diverged mirror (a ghost or
        // missing Jellyfin row) heals when the user retries instead of staying
        // unreachable behind the idempotence check.
        Assert.IsType<Ignored>(await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        Assert.Equal(2, store.WriteCallCount);
        Assert.Equal(mirrored.Id, Assert.Single(await MirroredAsync(store, itemId)).Id);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task UpdatedBoundaries_ReachJellyfinUnderTheSameId()
    {
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var itemId = Guid.NewGuid();
        var rowId = Assert.Single(Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20))).AffectedValues).Id;

        // The row keeps its id, so only the boundaries tell the mirror's
        // skip-when-unchanged comparison that Jellyfin is behind.
        Assert.Equal(ProjectionState.Applied, Assert.IsType<Accepted>(await service.ApplyAsync(
            new UpdateSegmentIntent(itemId, rowId, 15, 30))).Projection);

        var mirrored = Assert.Single(await MirroredAsync(store, itemId));
        Assert.Equal(rowId, mirrored.Id);
        Assert.Equal(15, mirrored.StartTicks);
        Assert.Equal(30, mirrored.EndTicks);
    }

    [Fact]
    public async Task NoReprojectProbe_DoesNotForceRunPendingWork()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore { WriteException = JellyfinDown() };
        var service = CreateService(store);

        Assert.Equal(ProjectionState.Pending, Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20))).Projection);

        // A probe of an id that exists in no state journals nothing, and it must not
        // force-project either: the item's unrelated pending work keeps the backoff
        // its failure earned instead of retrying on every stray 404.
        var probe = Assert.IsType<Ignored>(await service.ApplyAsync(
            new DeleteSegmentIntent(itemId, Guid.NewGuid())));

        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, probe.Reason);
        Assert.Equal(1, store.WriteCallCount);
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
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                SegmentChangeHarness.MirroredDto(itemId, tombstone.Id, MediaSegmentType.Intro, 10, 20),
                SegmentChangeHarness.MirroredDto(itemId, userRow.Id, MediaSegmentType.Intro, 50, 60),
            ],
        };
        var service = CreateService(store);

        var outcome = Assert.IsType<Ignored>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, tombstone.Id, MediaSegmentType.Intro)));

        // The suppressed shared-id row means the plugin already treats the segment as
        // deleted: the delete is idempotently ignored, the journaled op only removes
        // the lingering ghost row, and no fallback may hard-delete the user's own
        // (sole active) segment of the mode.
        Assert.Equal(SegmentChangeIgnoredReason.SegmentMissingOrDeleted, outcome.Reason);
        Assert.Empty(outcome.AffectedValues);
        Assert.Equal([(itemId, tombstone.Id)], store.DeletedSegments);
        Assert.Equal(userRow.Id, Assert.Single(await MirroredAsync(store, itemId)).Id);
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
        var service = CreateService(new FakeJellyfinSegmentStore { FindSegmentException = new InvalidOperationException("resolver unavailable") });

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
        var store = new FakeJellyfinSegmentStore { DeleteSegmentException = JellyfinDown() };
        AddForeign(store, itemId, externalId, MediaSegmentType.Intro, 10, 20);
        var service = CreateService(store);

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        Assert.Equal(ProjectionState.Pending, outcome.Projection);
        Assert.True(store.ForeignSegments.ContainsKey(externalId));
        await using var db = CreateContext();
        Assert.Equal(externalId, Assert.Single(await db.ProjectionExternalOperations.ToListAsync()).ExternalSegmentId);
        Assert.Equal(1, Assert.Single(await db.ProjectionQueue.ToListAsync()).AttemptCount);
    }

    [Fact]
    public async Task DisabledProjection_IsSkippedThenAppliedOnEnable()
    {
        var itemId = Guid.NewGuid();

        // A ghost row the enable replay has to remove, so the replay is visible in Jellyfin.
        var store = new FakeJellyfinSegmentStore { ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId)] };
        var policy = new FakeMirrorPolicy { Enabled = false };
        var service = CreateService(store, policy);

        var accepted = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));
        Assert.Equal(ProjectionState.Skipped, accepted.Projection);
        Assert.Equal(0, store.WriteCallCount);

        Assert.IsType<Accepted>(await service.ApplyAsync(new DeleteSegmentIntent(itemId, accepted.AffectedValues[0].Id)));
        await service.StartAsync(CancellationToken.None);
        policy.SetEnabled(true);
        await WaitForQueueEmptyAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(await MirroredAsync(store, itemId));
    }

    [Fact]
    public async Task SkippedExternalOperations_ReplayInOrderOnEnable()
    {
        var itemId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        AddForeign(store, itemId, firstId, MediaSegmentType.Intro, 10, 20);
        AddForeign(store, itemId, secondId, MediaSegmentType.Outro, 30, 40);
        var policy = new FakeMirrorPolicy { Enabled = false };
        var service = CreateService(store, policy);

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
        await WaitForQueueEmptyAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal([(itemId, firstId), (itemId, secondId)], store.DeletedSegments);
    }

    [Fact]
    public async Task StartupRecovery_AppliesPendingWorkIgnoringBackoff()
    {
        var itemId = Guid.NewGuid();
        await CreateService(new FakeJellyfinSegmentStore { WriteException = JellyfinDown() })
            .ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Preview, 10, 20));
        var recovering = new FakeJellyfinSegmentStore();
        var service = CreateService(recovering);

        await service.StartAsync(CancellationToken.None);
        await WaitForQueueEmptyAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.Single(await MirroredAsync(recovering, itemId));
    }

    [Fact]
    public async Task FailureForOneItem_DoesNotBlockAnotherItem()
    {
        var blockedItem = Guid.NewGuid();
        var progressingItem = Guid.NewGuid();

        // A gate released with an exception fails the blocked item's first write only.
        var failedGate = new TaskCompletionSource();
        failedGate.SetException(JellyfinDown());
        var store = new FakeJellyfinSegmentStore { WriteGate = failedGate, BlockedItemId = blockedItem };
        var service = CreateService(store);

        var blocked = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(blockedItem, AnalysisMode.Introduction, 10, 20)));
        var progressed = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(progressingItem, AnalysisMode.Credits, 30, 40)));

        Assert.Equal(ProjectionState.Pending, blocked.Projection);
        Assert.Equal(ProjectionState.Applied, progressed.Projection);
        Assert.Empty(await MirroredAsync(store, blockedItem));
        Assert.Single(await MirroredAsync(store, progressingItem));
    }

    [Fact]
    public async Task ChangesForOneItem_WaitForItsInFlightProjection()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = itemId,
        };
        var service = CreateService(store);

        // The first change parks inside its projection's Jellyfin write while holding
        // the item's stripe. The second change for the item must not commit, let alone
        // project, until that projection finished: a commit landing mid-apply would let
        // the parked write push an image older than the database.
        var first = service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20));
        await store.WriteEntered!.Task;
        var second = service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40));

        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200))));
        Assert.Single(await SegmentsAsync());

        store.WriteGate!.SetResult();
        Assert.Equal(ProjectionState.Applied, Assert.IsType<Accepted>(await first).Projection);
        Assert.Equal(ProjectionState.Applied, Assert.IsType<Accepted>(await second).Projection);
        Assert.Equal(2, (await MirroredAsync(store, itemId)).Count);
    }

    [Fact]
    public async Task ProjectionsOfOneItem_RunOneAtATime()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = itemId,
        };
        var service = CreateService(store);

        // The immediate projection parks inside its Jellyfin write. A second pass over
        // the same item (the retry loop, startup recovery, a maintenance convergence)
        // must wait for it: run concurrently, it would read the same pending work, write
        // a second time, and make the first pass's completion miss its version.
        var first = service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20));
        await store.WriteEntered!.Task;
        var pass = service.ProjectItemsAsync([itemId]);

        Assert.NotSame(pass, await Task.WhenAny(pass, Task.Delay(TimeSpan.FromMilliseconds(200))));
        Assert.Equal(1, store.WriteCallCount);

        store.WriteGate!.SetResult();
        Assert.Equal(ProjectionState.Applied, Assert.IsType<Accepted>(await first).Projection);
        await pass;
        Assert.Equal(1, store.WriteCallCount);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task InFlightProjection_DoesNotBlockOtherItems()
    {
        var parkedItem = Guid.NewGuid();
        var otherItem = NewGuidOnDifferentStripe(parkedItem);
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = parkedItem,
        };
        var service = CreateService(store);

        // The parked change already migrated and warmed the database, so the window
        // below measures stripe independence alone.
        var parked = service.ApplyAsync(new AddUserSegmentIntent(parkedItem, AnalysisMode.Introduction, 10, 20));
        await store.WriteEntered!.Task;
        var other = service.ApplyAsync(new AddUserSegmentIntent(otherItem, AnalysisMode.Introduction, 10, 20));

        Assert.Same(other, await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.Equal(ProjectionState.Applied, Assert.IsType<Accepted>(await other).Projection);
        Assert.False(parked.IsCompleted);

        store.WriteGate!.SetResult();
        await parked;
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
        Assert.Equal(staleVersion + 1, surviving.Value.Item.Version);

        Assert.True(await journal.CompleteProjectionWorkAsync(itemId, surviving.Value.Item.Version, [], CancellationToken.None));
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
        Assert.Null(current.Value.Item.NextAttemptAt);
        Assert.Equal(0, current.Value.Item.AttemptCount);
    }

    [Fact]
    public async Task CompletionSupersededMidApply_ReportsPendingNotApplied()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = itemId,
        };
        var service = CreateService(store);

        // An analyzer write holds no projection stripe, so it can land while a
        // projection is mid-apply: the marker's version bumps and the completion
        // must miss it, and the outcome must say Pending, not Applied, so retry
        // counts and the HTTP 202 mapping agree with the surviving marker.
        var applying = service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20));
        await store.WriteEntered!.Task;
        await CreateDatabase().ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(30, 40))], SegmentSource.Chapter);
        store.WriteGate!.SetResult();

        var outcome = Assert.IsType<Accepted>(await applying);
        Assert.Equal(ProjectionState.Pending, outcome.Projection);
        await using (var db = CreateContext())
        {
            Assert.Equal(2, Assert.Single(await db.ProjectionQueue.ToListAsync()).Version);
        }

        // With no further interleaving the surviving work converges on retry.
        Assert.Equal(1, await service.ProjectItemsAsync([itemId]));
        Assert.Equal(2, (await MirroredAsync(store, itemId)).Count);
        await AssertQueueEmptyAsync();
    }

    [Fact]
    public async Task Rebuild_RetainsPendingWorkAndOperations()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore { DeleteSegmentException = JellyfinDown() };
        AddForeign(store, itemId, externalId, MediaSegmentType.Intro, 10, 20);
        await CreateService(store).ApplyAsync(new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro));

        await CreateDatabase().RebuildDatabaseAsync();

        await using var verify = CreateContext();
        Assert.Single(await verify.ProjectionQueue.ToListAsync());
        Assert.Equal(externalId, Assert.Single(await verify.ProjectionExternalOperations.ToListAsync()).ExternalSegmentId);
    }

    /// <inheritdoc />
    public void Dispose() => _db.Dispose();

    private static InvalidOperationException JellyfinDown() => new("jellyfin down");

    /// <summary>Puts another provider's row into Jellyfin, replacing any row under the same id.</summary>
    private static void AddForeign(FakeJellyfinSegmentStore store, Guid itemId, Guid segmentId, MediaSegmentType type, long startTicks, long endTicks)
        => store.ForeignSegments[segmentId] = SegmentChangeHarness.MirroredDto(itemId, segmentId, type, startTicks, endTicks);

    /// <summary>Gets the item's Intro Skipper rows as Jellyfin holds them now.</summary>
    private static Task<IReadOnlyList<MediaSegmentDto>> MirroredAsync(FakeJellyfinSegmentStore store, Guid itemId)
        => store.GetOwnSegmentsAsync(itemId, CancellationToken.None);

    /// <summary>
    /// Picks an id on a different lock stripe than <paramref name="other"/>, so
    /// cross-item concurrency assertions cannot flake on a stripe collision.
    /// </summary>
    private static Guid NewGuidOnDifferentStripe(Guid other)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (StripedAsyncLock.StripeIndex(id) == StripedAsyncLock.StripeIndex(other));

        return id;
    }

    private SegmentChange CreateService(FakeJellyfinSegmentStore store, FakeMirrorPolicy? policy = null)
        => DatabaseTestHelpers.CreateSegmentChange(store, CreateDatabase(), policy ?? new FakeMirrorPolicy());

    private IntroSkipperDatabase CreateDatabase() => _db.CreateDatabase();

    private IntroSkipperDbContext CreateContext() => _db.Context();

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
    private async Task<(IntroSkipperDatabase Journal, Guid ItemId, long StaleVersion)> SeedSupersededWorkAsync()
    {
        var itemId = Guid.NewGuid();
        var database = CreateDatabase();

        Assert.Null((await database.ApplyChangeAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20))).Outcome);
        var work = await database.ReadProjectionWorkAsync(itemId, CancellationToken.None);
        Assert.NotNull(work);
        Assert.Null((await database.ApplyChangeAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40))).Outcome);
        return (database, itemId, work.Value.Item.Version);
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

    /// <summary>
    /// Waits until the background worker has drained the journal, queue markers and
    /// external operations alike: the only signal a replay that writes nothing to
    /// Jellyfin leaves behind.
    /// </summary>
    private async Task WaitForQueueEmptyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await using (var db = CreateContext())
            {
                if (!await db.ProjectionQueue.AnyAsync() && !await db.ProjectionExternalOperations.AnyAsync())
                {
                    return;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "The projection worker did not drain the journal within 10 seconds.");
            await Task.Delay(20);
        }
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
}
