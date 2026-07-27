// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.MediaSegments;
using Xunit;

public sealed class TestMediaSegmentEditorService
{
    [Fact]
    public async Task SyncItemAsync_UsesUniformReplace_ForEveryMode()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(1200, 1260))], SegmentSource.Chromaprint);
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Commercial,
            [new Segment(itemId, new TimeRange(300, 330)), new Segment(itemId, new TimeRange(600, 630))],
            SegmentSource.BlackFrame);
        await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Recap, TickConversions.FromSeconds(20), TickConversions.FromSeconds(40));
        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(5, rows.Count);

        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store, database);

        await service.SyncItemAsync(CreateMovie(itemId), CancellationToken.None);

        // One uniform replace carries every active segment of every mode — no per-type
        // routing, no commercial special case — and each DTO reuses its plugin row's id.
        Assert.Equal(1, store.WriteCallCount);
        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Equal(rows.Count, pushed.Count);
        foreach (var row in rows)
        {
            var dto = Assert.Single(pushed, segment => segment.Id == row.Id);
            Assert.Equal(itemId, dto.ItemId);
            Assert.Equal(row.StartTicks, dto.StartTicks);
            Assert.Equal(row.EndTicks, dto.EndTicks);
        }

        Assert.Equal(2, pushed.Count(segment => segment.Type == MediaSegmentType.Commercial));
    }

    [Fact]
    public async Task SyncItemAsync_SerializesConcurrentCallsForSameItem()
    {
        var item = CreateMovie(Guid.NewGuid());
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteEntered = writeEntered,
            BlockedItemId = item.Id
        };
        var service = CreateService(store);

        // First call enters the critical section and parks inside the store write while
        // holding the lock; wait for the park so the assertions below cannot race the
        // asynchronous DTO-factory read that precedes the write.
        var first = service.SyncItemAsync(item, CancellationToken.None);
        await writeEntered.Task;

        // Second call for the same item must block on the per-item lock and therefore must
        // not have reached the store yet.
        var second = service.SyncItemAsync(item, CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, store.WriteCallCount);

        store.WriteGate!.SetResult();
        await first;
        await second;

        Assert.Equal(2, store.WriteCallCount);
        Assert.Equal(2, store.ReplacedItems.Count);
    }

    [Fact]
    public async Task SyncItemAsync_AllowsConcurrentCallsForDifferentItems()
    {
        var firstItem = CreateMovie(Guid.NewGuid());
        var secondItem = CreateMovie(Guid.NewGuid());
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = firstItem.Id
        };
        var service = CreateService(store);

        var first = service.SyncItemAsync(firstItem, CancellationToken.None);
        var second = service.SyncItemAsync(secondItem, CancellationToken.None);

        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.False(first.IsCompleted);

        store.WriteGate!.SetResult();
        await first;

        Assert.Equal(2, store.WriteCallCount);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsMatchingSegment()
    {
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                CreateSegment(MediaSegmentType.Outro, 30, 40, Guid.NewGuid(), itemId),
                CreateSegment(MediaSegmentType.Intro, 10, 20, segmentId, itemId)
            ]
        };
        var service = CreateService(store);

        var result = await service.GetSegmentAsync(itemId, segmentId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(segmentId, result!.Id);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsNull_WhenItemIdDoesNotMatch()
    {
        var segmentId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, segmentId, Guid.NewGuid())]
        };
        var service = CreateService(store);

        Assert.Null(await service.GetSegmentAsync(Guid.NewGuid(), segmentId, CancellationToken.None));
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsNull_WhenSegmentIdDoesNotMatch()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, Guid.NewGuid(), itemId)]
        };
        var service = CreateService(store);

        var result = await service.GetSegmentAsync(itemId, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSegmentAsync_Throws_WhenCancelled()
    {
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, Guid.NewGuid())]
        };
        var service = CreateService(store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetSegmentAsync(Guid.NewGuid(), Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task DeleteSegmentAsync_DelegatesToStore()
    {
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();

        await service.DeleteSegmentAsync(itemId, segmentId, CancellationToken.None);

        Assert.Equal([(itemId, segmentId)], store.DeletedSegments);
    }

    private static MediaSegmentEditorService CreateService(
        FakeJellyfinSegmentStore store,
        IntroSkipper.Db.IIntroSkipperDatabase? database = null)
        => new(store, new SegmentDtoFactory(database ?? DatabaseTestHelpers.CreateTempSegmentDatabase()));

    private static Movie CreateMovie(Guid id)
    {
        var item = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(item, "Id", id);
        EntrypointTestHelpers.EnsureNonVirtual(item);
        return item;
    }

    private static MediaSegmentDto CreateSegment(MediaSegmentType type, long startTicks, long endTicks, Guid id = default, Guid itemId = default)
        => new()
        {
            Id = id,
            ItemId = itemId,
            Type = type,
            StartTicks = startTicks,
            EndTicks = endTicks
        };
}
