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

/// <summary>Durability and ordering tests for the segment change coordinator.</summary>
public sealed class TestSegmentChange : IDisposable
{
    private readonly string _dbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-changes.db");

    [Fact]
    public async Task AddUserSegment_CommitsImageAndCompactsAppliedPlan()
    {
        var adapter = new RecordingProjectionAdapter();
        var service = CreateService(adapter);
        var itemId = Guid.NewGuid();

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        Assert.Equal(ProjectionState.Applied, Assert.Single(outcome.Projections).State);
        var affected = Assert.Single(outcome.AffectedValues);
        var plan = Assert.Single(adapter.Plans);
        Assert.Equal(affected.Id, Assert.Single(plan.Segments).Id);
        Assert.Equal(SegmentSource.User, Assert.Single(plan.Segments).Source);

        await using var db = CreateContext();
        Assert.Empty(await db.ProjectionPlans.ToListAsync());
        Assert.Empty(await db.ProjectionAttempts.ToListAsync());
        var head = Assert.Single(await db.ProjectionHeads.ToListAsync());
        Assert.Equal(1, head.LastAcceptedSequence);
        Assert.Equal(1, head.LastAppliedSequence);
        Assert.Equal(ProjectionState.Applied, head.Status);
    }

    [Fact]
    public async Task ProjectionFailure_DoesNotRollBackAuthoritativeMutation_AndManualRetryConverges()
    {
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        var service = CreateService(adapter);
        var itemId = Guid.NewGuid();

        var outcome = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40)));
        Assert.Equal(ProjectionState.Pending, Assert.Single(outcome.Projections).State);

        await using (var db = CreateContext())
        {
            Assert.Single(await db.Segments.ToListAsync());
            Assert.Single(await db.ProjectionPlans.ToListAsync());
            var attempt = Assert.Single(await db.ProjectionAttempts.ToListAsync());
            Assert.Equal(1, attempt.AttemptCount);
            Assert.NotNull(attempt.Failure);
        }

        Assert.Equal(1, (await service.RetryProjectionAsync(ProjectionScope.ForItem(itemId))).RetriedCount);
        Assert.Equal(2, adapter.Attempts.Count);
        Assert.Equal(ProjectionState.Applied, Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items).State);
    }

    [Fact]
    public async Task FailedEarlierPlan_BlocksLaterPlanForSameItem()
    {
        var adapter = new RecordingProjectionAdapter { FailuresRemaining = 2 };
        var service = CreateService(adapter);
        var itemId = Guid.NewGuid();

        await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20));
        await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Credits, 30, 40));

        Assert.Equal(new long[] { 1, 1 }, adapter.Attempts.Select(plan => plan.Sequence));
        Assert.All(adapter.Attempts, plan => Assert.Single(plan.Segments));
        adapter.FailuresRemaining = 0;
        Assert.Equal(2, (await service.RetryProjectionAsync(ProjectionScope.ForItem(itemId))).RetriedCount);
        Assert.Equal(new long[] { 1, 1, 1, 2 }, adapter.Attempts.Select(plan => plan.Sequence));
        Assert.Single(adapter.Attempts[2].Segments);
        Assert.Equal(2, adapter.Attempts[3].Segments.Count);
        Assert.Equal(2, Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items).LastAppliedSequence);
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
        Assert.Empty(Assert.Single(adapter.Plans).Segments);
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
        Assert.Empty(await db.ProjectionPlans.ToListAsync());
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

        Assert.Equal(ProjectionState.Pending, Assert.Single(outcome.Projections).State);
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
        Assert.Empty(await db.AnalyzedItems.ToListAsync());
    }

    [Fact]
    public async Task AddPromotion_ClearsPriorAnalysisRecord()
    {
        var itemId = Guid.NewGuid();
        var automatic = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter, "automatic-hash");
        await SeedAsync(automatic);
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(itemId, AnalysisMode.Introduction, "automatic-hash"));

        var accepted = Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));

        Assert.Equal(automatic.Id, Assert.Single(accepted.AffectedValues).Id);
        await AssertNoAnalyzedItemAsync(itemId, AnalysisMode.Introduction);
    }

    [Fact]
    public async Task ReplaceUserSegments_ClearsPriorAnalysisRecord()
    {
        var itemId = Guid.NewGuid();
        await SeedAsync(new DbSegment(itemId, AnalysisMode.Credits, 1, 2, SegmentSource.BlackFrame, "old-hash"));

        await CreateService(new RecordingProjectionAdapter()).ApplyAsync(new ReplaceUserSegmentsForModeIntent(
            itemId,
            AnalysisMode.Credits,
            [new SegmentRange(30, 40), new SegmentRange(50, 60)]));

        await AssertNoAnalyzedItemAsync(itemId, AnalysisMode.Credits);
    }

    [Fact]
    public async Task ReplaceUserSegments_EmptyImage_RemovesAnalyzedState()
    {
        var itemId = Guid.NewGuid();
        await SeedAsync(new DbSegment(itemId, AnalysisMode.Commercial, 10, 20, SegmentSource.User));
        await SeedAnalyzedItemAsync(new DbAnalyzedItem(itemId, AnalysisMode.Commercial, "old"));

        Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new ReplaceUserSegmentsForModeIntent(itemId, AnalysisMode.Commercial, [])));

        await using var db = CreateContext();
        Assert.Empty(await db.AnalyzedItems.ToListAsync());
        Assert.Empty(await db.Segments.Where(row => row.State == SegmentState.Active).ToListAsync());
    }

    [Fact]
    public async Task UpdateCollision_ClearsPriorAnalysisRecord()
    {
        var itemId = Guid.NewGuid();
        var moved = new DbSegment(itemId, AnalysisMode.Preview, 10, 20, SegmentSource.Chapter, "one");
        var occupant = new DbSegment(itemId, AnalysisMode.Preview, 30, 40, SegmentSource.BlackFrame, "two");
        await SeedAsync(moved, occupant);

        var accepted = Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new UpdateSegmentIntent(itemId, moved.Id, occupant.StartTicks, occupant.EndTicks)));

        Assert.Equal(occupant.Id, Assert.Single(accepted.AffectedValues).Id);
        await AssertNoAnalyzedItemAsync(itemId, AnalysisMode.Preview);
    }

    [Fact]
    public async Task Delete_DerivesStateFromRemainingUserAndAutomaticRows()
    {
        var userItemId = Guid.NewGuid();
        var deletedUser = new DbSegment(userItemId, AnalysisMode.Commercial, 10, 20, SegmentSource.User);
        await SeedAsync(deletedUser, new DbSegment(userItemId, AnalysisMode.Commercial, 30, 40, SegmentSource.User));
        var service = CreateService(new RecordingProjectionAdapter());

        await service.ApplyAsync(new DeleteSegmentIntent(userItemId, deletedUser.Id));
        await AssertNoAnalyzedItemAsync(userItemId, AnalysisMode.Commercial);

        var autoItemId = Guid.NewGuid();
        var deletedAuto = new DbSegment(autoItemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter, "deleted");
        await SeedAsync(
            deletedAuto,
            new DbSegment(autoItemId, AnalysisMode.Introduction, 30, 40, SegmentSource.Chapter, "hash-a"),
            new DbSegment(autoItemId, AnalysisMode.Introduction, 50, 60, SegmentSource.BlackFrame, "hash-b"));

        await service.ApplyAsync(new DeleteSegmentIntent(autoItemId, deletedAuto.Id));
        await AssertAnalyzedItemAsync(autoItemId, AnalysisMode.Introduction, string.Empty);
    }

    [Fact]
    public async Task Delete_WithSoleRemainingAutomaticHash_PreservesHash()
    {
        var itemId = Guid.NewGuid();
        var deleted = new DbSegment(itemId, AnalysisMode.Recap, 10, 20, SegmentSource.Chromaprint, "old");
        await SeedAsync(deleted, new DbSegment(itemId, AnalysisMode.Recap, 30, 40, SegmentSource.Chromaprint, "remaining"));

        await CreateService(new RecordingProjectionAdapter()).ApplyAsync(new DeleteSegmentIntent(itemId, deleted.Id));

        await AssertAnalyzedItemAsync(itemId, AnalysisMode.Recap, "remaining");
    }

    [Fact]
    public async Task RestoreAutomatic_UpsertsAnalyzedWithPreservedHash()
    {
        var itemId = Guid.NewGuid();
        var tombstone = new DbSegment(itemId, AnalysisMode.Credits, 10, 20, SegmentSource.BlackFrame, "restore-hash")
        {
            State = SegmentState.Suppressed
        };
        await SeedAsync(tombstone);

        await CreateService(new RecordingProjectionAdapter()).ApplyAsync(new RestoreSegmentIntent(itemId, tombstone.Id));

        await AssertAnalyzedItemAsync(itemId, AnalysisMode.Credits, "restore-hash");
    }

    [Fact]
    public async Task RestoreAutomatic_WithActiveUserSegment_ClearsAnalysisRecord()
    {
        var itemId = Guid.NewGuid();
        var automatic = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter, "restored-hash")
        {
            State = SegmentState.Suppressed
        };
        await SeedAsync(
            automatic,
            new DbSegment(itemId, AnalysisMode.Introduction, 30, 40, SegmentSource.User));

        Assert.IsType<Accepted>(await CreateService(new RecordingProjectionAdapter()).ApplyAsync(
            new RestoreSegmentIntent(itemId, automatic.Id)));

        await AssertNoAnalyzedItemAsync(itemId, AnalysisMode.Introduction);
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
        Assert.Empty(await verify.ProjectionPlans.ToListAsync());
        Assert.Empty(await verify.ProjectionHeads.ToListAsync());
    }

    [Fact]
    public async Task DisabledProjection_IsSkippedThenReconciledWithEmptyImageOnEnable()
    {
        var adapter = new RecordingProjectionAdapter();
        var configuration = new TestProjectionConfiguration { Enabled = false };
        var service = CreateService(adapter, configuration);
        var itemId = Guid.NewGuid();

        var accepted = Assert.IsType<Accepted>(await service.ApplyAsync(
            new AddUserSegmentIntent(itemId, AnalysisMode.Introduction, 10, 20)));
        Assert.Equal(ProjectionState.Skipped, Assert.Single(accepted.Projections).State);
        Assert.Empty(adapter.Attempts);

        Assert.IsType<Accepted>(await service.ApplyAsync(new DeleteSegmentIntent(itemId, accepted.AffectedValues[0].Id)));
        await service.StartAsync(CancellationToken.None);
        configuration.SetEnabled(true);
        var reconciled = await adapter.Applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(itemId, reconciled.ItemId);
        Assert.Empty(reconciled.Segments);
    }

    [Fact]
    public async Task ExactOperation_RemainsDurableWhileDisabled_AndRunsOnEnable()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20)
        };
        var configuration = new TestProjectionConfiguration { Enabled = false };
        var service = CreateService(adapter, configuration);

        var accepted = Assert.IsType<Accepted>(await service.ApplyAsync(
            new DeleteExternalSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));
        Assert.Equal(ProjectionState.Skipped, Assert.Single(accepted.Projections).State);
        await using (var db = CreateContext())
        {
            Assert.Single(await db.ProjectionExternalOperations.ToListAsync());
        }

        await service.StartAsync(CancellationToken.None);
        configuration.SetEnabled(true);
        var reconciled = await adapter.Applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(externalId, Assert.Single(reconciled.ExternalOperations).ExternalSegmentId);
        await using var verify = CreateContext();
        Assert.Empty(await verify.ProjectionExternalOperations.ToListAsync());
    }

    [Fact]
    public async Task SkippedExactOperations_ReplayInAcceptedSequence_WithoutAdvancingAppliedHead()
    {
        var itemId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var adapter = new RecordingProjectionAdapter();
        adapter.ExternalTargets[firstId] = new ExternalSegmentTarget(firstId, itemId, MediaSegmentType.Intro, 10, 20);
        adapter.ExternalTargets[secondId] = new ExternalSegmentTarget(secondId, itemId, MediaSegmentType.Outro, 30, 40);
        var configuration = new TestProjectionConfiguration { Enabled = false };
        var service = CreateService(adapter, configuration);

        await service.ApplyAsync(new DeleteExternalSegmentIntent(itemId, firstId, MediaSegmentType.Intro));
        await service.ApplyAsync(new DeleteExternalSegmentIntent(itemId, secondId, MediaSegmentType.Outro));

        var skipped = Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items);
        Assert.Equal(2, skipped.LastAcceptedSequence);
        Assert.Equal(0, skipped.LastAppliedSequence);
        Assert.Equal(ProjectionState.Skipped, skipped.State);
        await using (var db = CreateContext())
        {
            Assert.Equal(new long[] { 1, 2 }, await db.ProjectionExternalOperations.OrderBy(operation => operation.Sequence).Select(operation => operation.Sequence).ToArrayAsync());
        }

        await service.StartAsync(CancellationToken.None);
        configuration.SetEnabled(true);
        var replay = await adapter.Applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.RetryProjectionAsync(ProjectionScope.ForItem(itemId));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { firstId, secondId }, replay.ExternalOperations.Select(operation => operation.ExternalSegmentId));
        var applied = Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items);
        Assert.Equal(3, applied.LastAcceptedSequence);
        Assert.Equal(3, applied.LastAppliedSequence);
        Assert.Equal(ProjectionState.Applied, applied.State);
    }

    [Fact]
    public async Task StartupRecovery_AppliesPendingPlan()
    {
        var itemId = Guid.NewGuid();
        var failing = new RecordingProjectionAdapter { FailuresRemaining = 1 };
        await CreateService(failing).ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Preview, 10, 20));
        var recovering = new RecordingProjectionAdapter();
        var service = CreateService(recovering);

        await service.StartAsync(CancellationToken.None);
        var plan = await recovering.Applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.RetryProjectionAsync(ProjectionScope.ForItem(itemId));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(itemId, plan.ItemId);
        Assert.Equal(ProjectionState.Applied, Assert.Single((await service.GetProjectionStatusAsync(ProjectionScope.ForItem(itemId))).Items).State);
    }

    [Fact]
    public async Task RecoveryPoll_DoesNotReconcilePendingExternalOperation()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        await SeedAsync(
            new DbSegment(Guid.NewGuid(), AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter),
            new DbSegment(Guid.NewGuid(), AnalysisMode.Credits, 30, 40, SegmentSource.BlackFrame));
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Intro, 10, 20),
            FailuresRemaining = 1
        };
        var service = CreateService(adapter);

        await service.ApplyAsync(new DeleteExternalSegmentIntent(itemId, externalId, MediaSegmentType.Intro));
        adapter.FailuresRemaining = 1;
        await service.StartAsync(CancellationToken.None);
        var reconciled = await Task.WhenAny(adapter.Applied.Task, Task.Delay(TimeSpan.FromSeconds(1))) == adapter.Applied.Task;
        await service.StopAsync(CancellationToken.None);

        Assert.False(reconciled);
        Assert.Equal(new long[] { 1, 1 }, adapter.Attempts.Select(plan => plan.Sequence));
        Assert.All(adapter.Attempts, plan => Assert.Equal(externalId, Assert.Single(plan.ExternalOperations).ExternalSegmentId));
        await using var db = CreateContext();
        Assert.Equal(1, Assert.Single(await db.ProjectionPlans.ToListAsync()).Sequence);
        Assert.Equal(1, Assert.Single(await db.ProjectionExternalOperations.ToListAsync()).Sequence);
        var head = Assert.Single(await db.ProjectionHeads.ToListAsync());
        Assert.Equal(1, head.LastAcceptedSequence);
        Assert.Equal(ProjectionState.Pending, head.Status);
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

        Assert.Equal(ProjectionState.Pending, Assert.Single(blocked.Projections).State);
        Assert.Equal(ProjectionState.Applied, Assert.Single(progressed.Projections).State);
        Assert.Contains(adapter.Plans, plan => plan.ItemId == progressingItem);
    }

    [Fact]
    public async Task Rebuild_RetainsPendingPlansAttemptsAndHeads()
    {
        var itemId = Guid.NewGuid();
        var service = CreateService(new RecordingProjectionAdapter { FailuresRemaining = 1 });
        await service.ApplyAsync(new AddUserSegmentIntent(itemId, AnalysisMode.Commercial, 10, 20));

        await using (var db = CreateContext())
        {
            await db.RebuildDatabaseAsync(CreateContext);
        }

        await using var verify = CreateContext();
        Assert.Single(await verify.ProjectionPlans.ToListAsync());
        Assert.Single(await verify.ProjectionPlanSegments.ToListAsync());
        Assert.Single(await verify.ProjectionAttempts.ToListAsync());
        Assert.Single(await verify.ProjectionHeads.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_CorrelatedId_TombstonesAutomaticWithExactTicks()
    {
        var itemId = Guid.NewGuid();
        var row = new DbSegment(itemId, AnalysisMode.Introduction, 123456789, 987654321, SegmentSource.Chapter, "hash");
        await SeedAsync(row);
        var adapter = new RecordingProjectionAdapter { ResolveException = new InvalidOperationException("correlated delete must not resolve externally") };

        var accepted = Assert.IsType<Accepted>(await CreateService(adapter).ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, row.Id, MediaSegmentType.Intro)));

        var affected = Assert.Single(accepted.AffectedValues);
        Assert.Equal(row.Id, affected.Id);
        Assert.Equal(123456789, affected.StartTicks);
        Assert.Equal(987654321, affected.EndTicks);
        Assert.Equal(SegmentState.Suppressed, affected.State);
        Assert.Empty(Assert.Single(adapter.Plans).ExternalOperations);
        await using var db = CreateContext();
        Assert.Equal(SegmentState.Suppressed, Assert.Single(await db.Segments.ToListAsync()).State);
    }

    [Fact]
    public async Task EditorDelete_UncorrelatedId_UsesExactTickFallbackAndDurableExternalDelete()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var row = new DbSegment(itemId, AnalysisMode.Credits, 111111111, 222222222, SegmentSource.User);
        await SeedAsync(row);
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Outro, row.StartTicks, row.EndTicks)
        };

        var accepted = Assert.IsType<Accepted>(await CreateService(adapter).ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Outro)));

        Assert.Equal(row.Id, Assert.Single(accepted.AffectedValues).Id);
        var operation = Assert.Single(Assert.Single(adapter.Plans).ExternalOperations);
        Assert.Equal(externalId, operation.ExternalSegmentId);
        await using var db = CreateContext();
        Assert.Empty(await db.Segments.ToListAsync());
    }

    [Fact]
    public async Task EditorDelete_RejectsResolvedTargetMismatchWithoutMutation()
    {
        var itemId = Guid.NewGuid();
        var externalId = Guid.NewGuid();
        var row = new DbSegment(itemId, AnalysisMode.Introduction, 10, 20, SegmentSource.Chapter);
        await SeedAsync(row);
        var adapter = new RecordingProjectionAdapter
        {
            ExternalTarget = new ExternalSegmentTarget(externalId, itemId, MediaSegmentType.Outro, row.StartTicks, row.EndTicks)
        };

        var rejected = Assert.IsType<Rejected>(await CreateService(adapter).ApplyAsync(
            new EditorDeleteSegmentIntent(itemId, externalId, MediaSegmentType.Intro)));

        Assert.Equal(SegmentChangeRejectedReason.ExternalTypeMismatch, rejected.Reason);
        await using var db = CreateContext();
        Assert.Equal(SegmentState.Active, Assert.Single(await db.Segments.ToListAsync()).State);
        Assert.Empty(await db.ProjectionPlans.ToListAsync());
    }

    /// <inheritdoc />
    public void Dispose() => DatabaseTestHelpers.DeleteSqliteFiles(_dbPath);

    private SegmentChange CreateService(RecordingProjectionAdapter adapter, TestProjectionConfiguration? configuration = null)
    {
        var factory = new TestDbContextFactory<IntroSkipperDbContext>(() => new IntroSkipperDbContext(_dbPath));
        var database = new IntroSkipperDatabase(factory, NullLogger<IntroSkipperDatabase>.Instance);
        return new SegmentChange(factory, database, adapter, configuration ?? new TestProjectionConfiguration(), TimeProvider.System, NullLogger<SegmentChange>.Instance);
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

    private async Task AssertNoAnalyzedItemAsync(Guid itemId, AnalysisMode mode)
    {
        await using var db = CreateContext();
        Assert.False(await db.AnalyzedItems.AnyAsync(value => value.ItemId == itemId && value.Type == mode));
    }

    private sealed class TestProjectionConfiguration : ISegmentProjectionConfiguration
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
        public int FailuresRemaining { get; set; }

        public ExternalSegmentTarget? ExternalTarget { get; set; }

        public Exception? ResolveException { get; set; }

        public Dictionary<Guid, ExternalSegmentTarget> ExternalTargets { get; } = [];

        public HashSet<Guid> FailingItems { get; } = [];

        public TaskCompletionSource<SegmentProjectionPlan> Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SegmentProjectionPlan> Attempts { get; } = [];

        public List<SegmentProjectionPlan> Plans { get; } = [];

        public Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken)
        {
            if (ResolveException is not null)
            {
                throw ResolveException;
            }

            return Task.FromResult(ExternalTargets.TryGetValue(externalSegmentId, out var target) ? target : ExternalTarget);
        }

        public Task ApplyAsync(SegmentProjectionPlan plan, CancellationToken cancellationToken)
        {
            var snapshot = plan with
            {
                Segments = plan.Segments.ToArray(),
                ExternalOperations = plan.ExternalOperations.ToArray()
            };
            Attempts.Add(snapshot);
            if (FailuresRemaining-- > 0 || FailingItems.Contains(plan.ItemId))
            {
                throw new InvalidOperationException("synthetic projection failure");
            }

            Plans.Add(snapshot);
            Applied.TrySetResult(snapshot);
            return Task.CompletedTask;
        }
    }
}
