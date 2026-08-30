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
    public async Task ExternalDelete_RejectsCrossItemAndTypeBeforeCommit()
    {
        var itemId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(Guid.NewGuid(), Guid.NewGuid(), MediaSegmentType.Intro, 10, 20)
        };
        var service = CreateService(adapter);

        Assert.IsType<Rejected>(await service.ApplyAsync(
            new DeleteExternalSegmentIntent(itemId, adapter.ExternalTarget.Id, MediaSegmentType.Intro)));

        adapter.ExternalTarget = adapter.ExternalTarget with { ItemId = itemId };
        Assert.IsType<Rejected>(await service.ApplyAsync(
            new DeleteExternalSegmentIntent(itemId, adapter.ExternalTarget.Id, MediaSegmentType.Outro)));
        await using var db = CreateContext();
        await db.ApplyMigrationsAsync();
        Assert.Empty(await db.ProjectionQueue.ToListAsync());
        Assert.Empty(await db.ProjectionExternalOperations.ToListAsync());
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
    public async Task ExternalResolutionInfrastructureFailure_PropagatesWithoutMutation()
    {
        await using (var db = CreateContext())
        {
            await db.ApplyMigrationsAsync();
        }

        var adapter = new RecordingProjectionAdapter { ResolveException = new InvalidOperationException("resolver unavailable") };
        var service = CreateService(adapter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new DeleteExternalSegmentIntent(Guid.NewGuid(), Guid.NewGuid(), MediaSegmentType.Intro)));

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
            new DeleteExternalSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

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
        var policy = new TestMirrorPolicy { Enabled = false };
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
        var policy = new TestMirrorPolicy { Enabled = false };
        var service = CreateService(adapter, policy);

        await service.ApplyAsync(new DeleteExternalSegmentIntent(itemId, firstId, MediaSegmentType.Intro));
        await service.ApplyAsync(new DeleteExternalSegmentIntent(itemId, secondId, MediaSegmentType.Outro));

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
        await CreateService(adapter).ApplyAsync(new DeleteExternalSegmentIntent(itemId, externalId, MediaSegmentType.Intro));

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

    private SegmentChange CreateService(RecordingProjectionAdapter adapter, TestMirrorPolicy? policy = null)
    {
        var database = CreateDatabase();
        adapter.Database = database;
        return new SegmentChange(
            database,
            database,
            adapter,
            policy ?? new TestMirrorPolicy(),
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

    private sealed class TestMirrorPolicy : IMediaSegmentMirrorPolicy
    {
        public event EventHandler<bool>? EnabledChanged;

        public bool Enabled { get; set; } = true;

        internal void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            EnabledChanged?.Invoke(this, enabled);
        }
    }

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

        public Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken)
        {
            if (ResolveException is not null)
            {
                throw ResolveException;
            }

            return Task.FromResult(ExternalTargets.TryGetValue(externalSegmentId, out var target) ? target : ExternalTarget);
        }

        public async Task ApplyAsync(Guid itemId, IReadOnlyList<ProjectedExternalOperation> externalOperations, CancellationToken cancellationToken)
        {
            // Snapshot what the real adapter would push: the item's current servable
            // image (the disable filter applied), read through the facade.
            IReadOnlyList<DbSegment> image = Database is null
                ? []
                : await Database.GetServableSegmentsAsync(itemId, cancellationToken);
            var snapshot = new AppliedProjection(itemId, image, [.. externalOperations]);
            Attempts.Add(snapshot);
            if (FailuresRemaining-- > 0 || FailingItems.Contains(itemId))
            {
                throw new InvalidOperationException("synthetic projection failure");
            }

            Applies.Add(snapshot);
            Applied.TrySetResult(snapshot);
        }
    }
}
