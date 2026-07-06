using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestMediaSegmentEditorService
{
    [Fact]
    public async Task CreateOrReplaceSegmentAsync_DoesNothing_WhenProviderNotFound()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var manager = new FakeMediaSegmentManager { Providers = [("Some Other Provider", "x")] };
        var service = CreateService(manager);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(Guid.NewGuid()), CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);

        Assert.Equal(0, manager.GetSegmentsCallCount);
        Assert.Equal(0, manager.CreateCount);
        Assert.Empty(manager.DeletedSegmentIds);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_ReplacesExisting_ForNonCommercialType()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var existing1 = CreateSegment(MediaSegmentType.Intro, 0, 5, Guid.NewGuid());
        var existing2 = CreateSegment(MediaSegmentType.Intro, 5, 9, Guid.NewGuid());
        var manager = new FakeMediaSegmentManager
        {
            Providers = [(Plugin.Instance!.Name, "intro-skipper")],
            ExistingSegments = [existing1, existing2]
        };
        var service = CreateService(manager);
        var segment = CreateSegment(MediaSegmentType.Intro, 10, 20);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(itemId), segment, CancellationToken.None);

        Assert.Equal(2, manager.DeletedSegmentIds.Count);
        Assert.Contains(existing1.Id, manager.DeletedSegmentIds);
        Assert.Contains(existing2.Id, manager.DeletedSegmentIds);
        Assert.Equal(new[] { MediaSegmentType.Intro }, manager.LastTypeFilter);
        Assert.Equal(1, manager.CreateCount);
        Assert.Equal(itemId, manager.LastCreatedSegment!.ItemId);
        Assert.Equal("intro-skipper", manager.LastCreatedProviderId);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_SkipsCreate_WhenIdenticalCommercialExists()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var manager = new FakeMediaSegmentManager
        {
            Providers = [(Plugin.Instance!.Name, "intro-skipper")],
            ExistingSegments = [CreateSegment(MediaSegmentType.Commercial, 10, 20, Guid.NewGuid())]
        };
        var service = CreateService(manager);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(Guid.NewGuid()), CreateSegment(MediaSegmentType.Commercial, 10, 20), CancellationToken.None);

        Assert.Empty(manager.DeletedSegmentIds);
        Assert.Equal(0, manager.CreateCount);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_CreatesCommercial_WhenNoIdenticalExists()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var manager = new FakeMediaSegmentManager
        {
            Providers = [(Plugin.Instance!.Name, "intro-skipper")],
            ExistingSegments = [CreateSegment(MediaSegmentType.Commercial, 10, 20, Guid.NewGuid())]
        };
        var service = CreateService(manager);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(Guid.NewGuid()), CreateSegment(MediaSegmentType.Commercial, 30, 40), CancellationToken.None);

        // Commercial segments are never deleted; only the non-duplicate is added.
        Assert.Empty(manager.DeletedSegmentIds);
        Assert.Equal(1, manager.CreateCount);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_AllowsMultipleMovieCredits()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var existing = CreateSegment(MediaSegmentType.Outro, 10, 20, Guid.NewGuid());
        var manager = new FakeMediaSegmentManager
        {
            Providers = [(Plugin.Instance!.Name, "intro-skipper")],
            ExistingSegments = [existing]
        };
        var service = CreateService(manager);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(Guid.NewGuid()), CreateSegment(MediaSegmentType.Outro, 30, 40), CancellationToken.None);

        Assert.Empty(manager.DeletedSegmentIds);
        Assert.Equal(1, manager.CreateCount);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_ReplacesExistingTelevisionCredits()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var existing = CreateSegment(MediaSegmentType.Outro, 10, 20, Guid.NewGuid());
        var manager = new FakeMediaSegmentManager
        {
            Providers = [(Plugin.Instance!.Name, "intro-skipper")],
            ExistingSegments = [existing]
        };
        var service = CreateService(manager);

        await service.CreateOrReplaceSegmentAsync(CreateEpisode(Guid.NewGuid()), CreateSegment(MediaSegmentType.Outro, 30, 40), CancellationToken.None);

        Assert.Equal([existing.Id], manager.DeletedSegmentIds);
        Assert.Equal(1, manager.CreateCount);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_StillCreates_WhenDeletingExistingFails()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var manager = new FakeMediaSegmentManager
        {
            Providers = [(Plugin.Instance!.Name, "intro-skipper")],
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 0, 5, Guid.NewGuid())],
            DeleteException = new InvalidOperationException("delete failed")
        };
        var service = CreateService(manager);

        await service.CreateOrReplaceSegmentAsync(CreateMovie(Guid.NewGuid()), CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);

        Assert.Single(manager.DeletedSegmentIds);
        Assert.Equal(1, manager.CreateCount);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_SerializesConcurrentCallsForSameItem()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var item = CreateMovie(Guid.NewGuid());
        var gate = new TaskCompletionSource<IEnumerable<MediaSegmentDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new FakeMediaSegmentManager
        {
            Providers = [(Plugin.Instance!.Name, "intro-skipper")],
            FirstGetGate = gate,
            BlockedItemId = item.Id,
        };
        var service = CreateService(manager);

        // First call enters the critical section and parks inside GetSegmentsAsync while holding the lock.
        var first = service.CreateOrReplaceSegmentAsync(item, CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);

        // Second call for the same item must block on the per-item lock and therefore must not have
        // fetched segments yet.
        var second = service.CreateOrReplaceSegmentAsync(item, CreateSegment(MediaSegmentType.Intro, 30, 40), CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, manager.GetSegmentsCallCount);

        gate.SetResult([]);
        await first;
        await second;

        Assert.Equal(2, manager.GetSegmentsCallCount);
        Assert.Equal(2, manager.CreateCount);
    }

    [Fact]
    public async Task CreateOrReplaceSegmentAsync_AllowsConcurrentCallsForDifferentItems()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var firstItem = CreateMovie(Guid.NewGuid());
        var secondItem = CreateMovie(Guid.NewGuid());
        var gate = new TaskCompletionSource<IEnumerable<MediaSegmentDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new FakeMediaSegmentManager
        {
            Providers = [(Plugin.Instance!.Name, "intro-skipper")],
            FirstGetGate = gate,
            BlockedItemId = firstItem.Id,
        };
        var service = CreateService(manager);

        var first = service.CreateOrReplaceSegmentAsync(firstItem, CreateSegment(MediaSegmentType.Intro, 10, 20), CancellationToken.None);
        var second = service.CreateOrReplaceSegmentAsync(secondItem, CreateSegment(MediaSegmentType.Intro, 30, 40), CancellationToken.None);

        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.False(first.IsCompleted);

        gate.SetResult([]);
        await first;

        Assert.Equal(2, manager.GetSegmentsCallCount);
        Assert.Equal(2, manager.CreateCount);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsNull_ForEmptyItemId()
    {
        var manager = new FakeMediaSegmentManager();
        var service = CreateService(manager);

        var result = await service.GetSegmentAsync(Guid.Empty, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, manager.GetSegmentsCallCount);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsNull_WhenItemNotFound()
    {
        var manager = new FakeMediaSegmentManager();
        var service = CreateService(manager, EntrypointTestHelpers.CreateLibraryManager());

        var result = await service.GetSegmentAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, manager.GetSegmentsCallCount);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsMatchingSegment()
    {
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var item = CreateMovie(itemId);
        var manager = new FakeMediaSegmentManager
        {
            ExistingSegments =
            [
                CreateSegment(MediaSegmentType.Outro, 30, 40, Guid.NewGuid()),
                CreateSegment(MediaSegmentType.Intro, 10, 20, segmentId)
            ]
        };
        var service = CreateService(manager, EntrypointTestHelpers.CreateLibraryManager(item));

        var result = await service.GetSegmentAsync(itemId, segmentId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(segmentId, result!.Id);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsNull_WhenSegmentIdDoesNotMatch()
    {
        var itemId = Guid.NewGuid();
        var item = CreateMovie(itemId);
        var manager = new FakeMediaSegmentManager
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, Guid.NewGuid())]
        };
        var service = CreateService(manager, EntrypointTestHelpers.CreateLibraryManager(item));

        var result = await service.GetSegmentAsync(itemId, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSegmentAsync_Throws_WhenCancelled()
    {
        var itemId = Guid.NewGuid();
        var item = CreateMovie(itemId);
        var manager = new FakeMediaSegmentManager
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, Guid.NewGuid())]
        };
        var service = CreateService(manager, EntrypointTestHelpers.CreateLibraryManager(item));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.GetSegmentAsync(itemId, Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task DeleteSegmentAsync_DelegatesToManager()
    {
        var manager = new FakeMediaSegmentManager();
        var service = CreateService(manager);
        var segmentId = Guid.NewGuid();

        await service.DeleteSegmentAsync(segmentId);

        Assert.Equal([segmentId], manager.DeletedSegmentIds);
    }

    private static MediaSegmentEditorService CreateService(FakeMediaSegmentManager manager, ILibraryManager? libraryManager = null)
        => new(manager, libraryManager ?? EntrypointTestHelpers.CreateLibraryManager(), NullLogger<MediaSegmentEditorService>.Instance);

    private static Movie CreateMovie(Guid id)
    {
        var item = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(item, "Id", id);
        EntrypointTestHelpers.EnsureNonVirtual(item);
        return item;
    }

    private static Episode CreateEpisode(Guid id)
    {
        var item = new Episode();
        EntrypointTestHelpers.SetPropertyOrField(item, "Id", id);
        EntrypointTestHelpers.EnsureNonVirtual(item);
        return item;
    }

    private static MediaSegmentDto CreateSegment(MediaSegmentType type, long startTicks, long endTicks, Guid id = default)
        => new()
        {
            Id = id,
            Type = type,
            StartTicks = startTicks,
            EndTicks = endTicks
        };

    private sealed class FakeMediaSegmentManager : IMediaSegmentManager
    {
        private int _getCount;

        public IReadOnlyList<(string Name, string Id)> Providers { get; init; } = [];

        public IReadOnlyList<MediaSegmentDto> ExistingSegments { get; init; } = [];

        public Exception? DeleteException { get; init; }

        public TaskCompletionSource<IEnumerable<MediaSegmentDto>>? FirstGetGate { get; init; }
        public Guid? BlockedItemId { get; init; }

        public int GetSegmentsCallCount => _getCount;

        public int CreateCount { get; private set; }

        public MediaSegmentDto? LastCreatedSegment { get; private set; }

        public string? LastCreatedProviderId { get; private set; }

        public IReadOnlyList<MediaSegmentType>? LastTypeFilter { get; private set; }

        public List<Guid> DeletedSegmentIds { get; } = [];

        public IEnumerable<(string Name, string Id)> GetSupportedProviders(BaseItem item) => Providers;

        public Task<IEnumerable<MediaSegmentDto>> GetSegmentsAsync(BaseItem item, IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions libraryOptions, bool filterByProvider = true)
        {
            var n = Interlocked.Increment(ref _getCount);
            LastTypeFilter = typeFilter?.ToArray();

            if (FirstGetGate is not null && item.Id == BlockedItemId)
            {
                return FirstGetGate.Task;
            }

            return Task.FromResult<IEnumerable<MediaSegmentDto>>(ExistingSegments);
        }

        public Task<MediaSegmentDto> CreateSegmentAsync(MediaSegmentDto mediaSegment, string segmentProviderId)
        {
            CreateCount++;
            LastCreatedSegment = mediaSegment;
            LastCreatedProviderId = segmentProviderId;
            return Task.FromResult(mediaSegment);
        }

        public Task DeleteSegmentAsync(Guid segmentId)
        {
            lock (DeletedSegmentIds)
            {
                DeletedSegmentIds.Add(segmentId);
            }

            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            return Task.CompletedTask;
        }

        public Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunSegmentPluginProviders(BaseItem baseItem, LibraryOptions libraryOptions, bool forceOverwrite, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool HasSegments(Guid itemId) => false;

        public bool IsTypeSupported(BaseItem baseItem) => true;
    }
}
