// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.MediaSegments;
using Xunit;

public sealed class TestMediaSegmentEditorService
{
    [Fact]
    public async Task CreateOrReplaceSegmentAsync_RoutesNonCommercialToReplaceType()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var segment = CreateSegment(MediaSegmentType.Intro, 10, 20);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(itemId), segment, CancellationToken.None);

        var (replacedItemId, replacedSegment) = Assert.Single(store.ReplacedTypes);
        Assert.Equal(itemId, replacedItemId);
        Assert.Same(segment, replacedSegment);
        Assert.Empty(store.CreatedCommercials);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_RoutesCommercialToCreateIfAbsent()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store);
        var segment = CreateSegment(MediaSegmentType.Commercial, 10, 20);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(itemId), segment, CancellationToken.None);

        var (createdItemId, createdSegment) = Assert.Single(store.CreatedCommercials);
        Assert.Equal(itemId, createdItemId);
        Assert.Same(segment, createdSegment);
        Assert.Empty(store.ReplacedTypes);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_SerializesConcurrentCallsForSameItem()
    {
        var item = CreateMovie(Guid.NewGuid());
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = item.Id
        };
        var service = CreateService(store);

        // First call enters the critical section and parks inside the store write while holding the lock.
        var first = service.CreateOrReplaceSegmentAsync(item, CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);

        // Second call for the same item must block on the per-item lock and therefore must not have
        // reached the store yet.
        var second = service.CreateOrReplaceSegmentAsync(item, CreateSegment(MediaSegmentType.Intro, 30, 40), CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, store.WriteCallCount);

        store.WriteGate!.SetResult();
        await first;
        await second;

        Assert.Equal(2, store.WriteCallCount);
        Assert.Equal(2, store.ReplacedTypes.Count);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_AllowsConcurrentCallsForDifferentItems()
    {
        var firstItem = CreateMovie(Guid.NewGuid());
        var secondItem = CreateMovie(Guid.NewGuid());
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = firstItem.Id
        };
        var service = CreateService(store);

        var first = service.CreateOrReplaceSegmentAsync(firstItem, CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);
        var second = service.CreateOrReplaceSegmentAsync(secondItem, CreateSegment(MediaSegmentType.Intro, 30, 40), CancellationToken.None);

        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.False(first.IsCompleted);

        store.WriteGate!.SetResult();
        await first;

        Assert.Equal(2, store.WriteCallCount);
    }

    [Fact]
    public async Task GetSegmentByIdAsync_ReturnsMatchingSegment()
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

        var result = await service.GetSegmentByIdAsync(segmentId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(segmentId, result!.Id);
        Assert.Equal(itemId, result.ItemId);
    }

    [Fact]
    public async Task GetSegmentByIdAsync_ReturnsNull_WhenSegmentIdDoesNotMatch()
    {
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, Guid.NewGuid(), Guid.NewGuid())]
        };
        var service = CreateService(store);

        var result = await service.GetSegmentByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSegmentByIdAsync_Throws_WhenCancelled()
    {
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, Guid.NewGuid())]
        };
        var service = CreateService(store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetSegmentByIdAsync(Guid.NewGuid(), cts.Token));
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

    private static MediaSegmentEditorService CreateService(FakeJellyfinSegmentStore store)
        => new(store);

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
