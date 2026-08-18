// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>Contract tests for atomic application of durable Jellyfin plans.</summary>
public sealed class TestJellyfinSegmentProjectionAdapter
{
    [Fact]
    public async Task ApplyAsync_DeletesExactTargetThenReplacesOwnProviderRows()
    {
        using var db = new TempJellyfinDb();
        var itemId = Guid.NewGuid();
        var target = Row(itemId, MediaSegmentType.Intro, 1, 2, "foreign");
        var survivor = Row(itemId, MediaSegmentType.Outro, 3, 4, "foreign");
        await SeedAsync(db, target, survivor, Row(itemId, MediaSegmentType.Intro, 5, 6, JellyfinSegmentStore.ProviderId));
        var projectedId = Guid.NewGuid();

        await Create(db).ApplyAsync(new SegmentProjectionPlan(
            Guid.NewGuid(),
            itemId,
            1,
            [new ProjectedSegment(projectedId, MediaSegmentType.Recap, 10, 20, SegmentSource.User)],
            [new ProjectedExternalOperation(target.Id, target.Type, ProjectionExternalOperationKind.Delete)]),
            CancellationToken.None);

        await using var verify = db.Factory.CreateDbContext();
        var rows = await verify.MediaSegments.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(rows, row => row.Id == target.Id);
        Assert.Contains(rows, row => row.Id == survivor.Id);
        var own = Assert.Single(rows, row => row.SegmentProviderId == JellyfinSegmentStore.ProviderId);
        Assert.Equal(projectedId, own.Id);
        Assert.Equal(MediaSegmentType.Recap, own.Type);
    }

    [Fact]
    public async Task ApplyAsync_IsIdempotentAfterJellyfinCommit()
    {
        using var db = new TempJellyfinDb();
        var itemId = Guid.NewGuid();
        var target = Row(itemId, MediaSegmentType.Intro, 1, 2, "foreign");
        await SeedAsync(db, target);
        var plan = new SegmentProjectionPlan(
            Guid.NewGuid(),
            itemId,
            1,
            [new ProjectedSegment(Guid.NewGuid(), MediaSegmentType.Intro, 10, 20, SegmentSource.User)],
            [new ProjectedExternalOperation(target.Id, target.Type, ProjectionExternalOperationKind.Delete)]);
        var adapter = Create(db);

        await adapter.ApplyAsync(plan, CancellationToken.None);
        await adapter.ApplyAsync(plan, CancellationToken.None);

        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(plan.Segments[0].Id, Assert.Single(await verify.MediaSegments.AsNoTracking().ToListAsync()).Id);
    }

    [Fact]
    public async Task ApplyAsync_EmptyImageDeletesOwnRowsOnly()
    {
        using var db = new TempJellyfinDb();
        var itemId = Guid.NewGuid();
        var foreign = Row(itemId, MediaSegmentType.Intro, 1, 2, "foreign");
        await SeedAsync(db, foreign, Row(itemId, MediaSegmentType.Outro, 3, 4, JellyfinSegmentStore.ProviderId));

        await Create(db).ApplyAsync(new SegmentProjectionPlan(Guid.NewGuid(), itemId, 1, [], []), CancellationToken.None);

        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(foreign.Id, Assert.Single(await verify.MediaSegments.AsNoTracking().ToListAsync()).Id);
    }

    [Fact]
    public async Task ResolveExternalTargetAsync_ReturnsActualOwnerAndType()
    {
        using var db = new TempJellyfinDb();
        var row = Row(Guid.NewGuid(), MediaSegmentType.Commercial, 10, 20, "foreign");
        await SeedAsync(db, row);

        var target = await Create(db).ResolveExternalTargetAsync(Guid.NewGuid(), row.Id, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(row.ItemId, target.ItemId);
        Assert.Equal(row.Type, target.Type);
        Assert.Equal(row.StartTicks, target.StartTicks);
        Assert.Equal(row.EndTicks, target.EndTicks);
    }

    [Fact]
    public async Task FailedApply_RollsBackEveryOperationAndReleasesWriteLock()
    {
        using var db = new TempJellyfinDb(new PessimisticLockBehavior(
            NullLogger<PessimisticLockBehavior>.Instance,
            NullLoggerFactory.Instance));
        var itemId = Guid.NewGuid();
        var target = Row(itemId, MediaSegmentType.Intro, 1, 2, "foreign");
        var collision = Row(itemId, MediaSegmentType.Outro, 3, 4, "foreign");
        var own = Row(itemId, MediaSegmentType.Recap, 5, 6, JellyfinSegmentStore.ProviderId);
        await SeedAsync(db, target, collision, own);
        var adapter = Create(db);
        var failingPlan = new SegmentProjectionPlan(
            Guid.NewGuid(),
            itemId,
            1,
            [new ProjectedSegment(collision.Id, MediaSegmentType.Commercial, 10, 20, SegmentSource.User)],
            [new ProjectedExternalOperation(target.Id, target.Type, ProjectionExternalOperationKind.Delete)]);

        await Assert.ThrowsAsync<DbUpdateException>(() => adapter.ApplyAsync(failingPlan, CancellationToken.None));
        await adapter.ApplyAsync(new SegmentProjectionPlan(Guid.NewGuid(), itemId, 2, [], []), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await using var verify = db.Factory.CreateDbContext();
        var rows = await verify.MediaSegments.AsNoTracking().ToListAsync();
        Assert.Contains(rows, row => row.Id == target.Id);
        Assert.Contains(rows, row => row.Id == collision.Id);
        Assert.DoesNotContain(rows, row => row.Id == own.Id);
    }

    private static JellyfinSegmentProjectionAdapter Create(TempJellyfinDb db)
        => new(db.Factory, NullLogger<JellyfinSegmentProjectionAdapter>.Instance);

    private static MediaSegment Row(Guid itemId, MediaSegmentType type, long start, long end, string provider)
        => new() { Id = Guid.NewGuid(), ItemId = itemId, Type = type, StartTicks = start, EndTicks = end, SegmentProviderId = provider };

    private static async Task SeedAsync(TempJellyfinDb db, params MediaSegment[] rows)
    {
        await using var context = db.Factory.CreateDbContext();
        context.MediaSegments.AddRange(rows);
        await context.SaveChangesAsync();
    }
}
