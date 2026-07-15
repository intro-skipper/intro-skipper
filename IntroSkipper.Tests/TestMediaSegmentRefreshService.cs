using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestMediaSegmentRefreshService
{
    [Fact]
    public async Task RefreshAsync_AwaitsRunSegmentPluginProviders_BeforeCompleting()
    {
        var itemId = Guid.NewGuid();
        var item = CreateMovie(itemId);
        var manager = new FakeMediaSegmentManager
        {
            RunCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var refresher = CreateRefresher(manager);

        var refreshTask = refresher.RefreshAsync(item, CancellationToken.None);

        Assert.False(refreshTask.IsCompleted);

        manager.RunCompletion.SetResult();
        await refreshTask;

        Assert.Equal(1, manager.RunCount);
        Assert.Equal(itemId, manager.LastItemId);
        Assert.True(manager.LastForceOverwrite.GetValueOrDefault());
    }

    [Fact]
    public async Task RefreshAsync_UsesExternalProvidersAndForceOverwrite()
    {
        var manager = new FakeMediaSegmentManager();
        var refresher = CreateRefresher(manager);

        await refresher.RefreshAsync(CreateMovie(Guid.NewGuid()), CancellationToken.None);

        Assert.NotNull(manager.LastLibraryOptions);
        Assert.Contains("Chapter Segments Provider", manager.LastLibraryOptions!.DisabledMediaSegmentProviders);
        Assert.True(manager.LastForceOverwrite.GetValueOrDefault());
    }

    [Fact]
    public async Task RefreshAsync_LogsAndReturnsAfterProviderFailure()
    {
        var itemId = Guid.NewGuid();
        var manager = new FakeMediaSegmentManager
        {
            RunException = new InvalidOperationException("boom")
        };
        var refresher = CreateRefresher(manager);

        await refresher.RefreshAsync(CreateMovie(itemId), CancellationToken.None);

        Assert.Equal(1, manager.RunCount);
        Assert.Equal(itemId, manager.LastItemId);
        Assert.True(manager.LastForceOverwrite.GetValueOrDefault());
    }

    [Fact]
    public async Task RefreshAsync_RethrowsCriticalException()
    {
        var manager = new FakeMediaSegmentManager
        {
            RunException = new ThreadInterruptedException()
        };
        var refresher = CreateRefresher(manager);

        await Assert.ThrowsAsync<ThreadInterruptedException>(
            () => refresher.RefreshAsync(CreateMovie(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(1, manager.RunCount);
    }

    [Fact]
    public async Task RefreshAsync_ByIds_ResolvesItemsViaLibraryManager_SkippingEmptyAndDuplicateIds()
    {
        var itemId = Guid.NewGuid();
        var item = CreateMovie(itemId);
        var manager = new FakeMediaSegmentManager();
        var libraryManager = EntrypointTestHelpers.CreateLibraryManager(item);
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { MaxParallelism = 2 });
        var refresher = CreateRefresher(manager, libraryManager);

        await refresher.RefreshAsync([itemId, Guid.Empty, itemId], CancellationToken.None);

        Assert.Equal(1, manager.RunCount);
        Assert.Equal(itemId, manager.LastItemId);
    }

    [Fact]
    public async Task RemoveIntroSkipperSegmentsAsync_DeletesOnlyIntroSkipperSegments()
    {
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var manager = new FakeMediaSegmentManager
        {
            SegmentsToReturn = [new MediaSegmentDto { Id = segmentId, ItemId = itemId }]
        };
        var libraryManager = EntrypointTestHelpers.CreateLibraryManager(CreateMovie(itemId));
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { MaxParallelism = 2 });
        var refresher = CreateRefresher(manager, libraryManager);

        await refresher.RemoveIntroSkipperSegmentsAsync([itemId], CancellationToken.None);

        Assert.Equal([segmentId], manager.DeletedSegmentIds);
        Assert.NotNull(manager.LastLibraryOptions);
        Assert.DoesNotContain(Plugin.ProviderName, manager.LastLibraryOptions!.DisabledMediaSegmentProviders);
        Assert.Contains("Chapter Segments Provider", manager.LastLibraryOptions.DisabledMediaSegmentProviders);
        Assert.True(manager.LastFilterByProvider);
        Assert.Equal(0, manager.RunCount);
    }

    [Fact]
    public async Task RemoveIntroSkipperSegmentsAsync_PropagatesDeleteFailure()
    {
        var itemId = Guid.NewGuid();
        var expectedException = new InvalidOperationException("boom");
        var manager = new FakeMediaSegmentManager
        {
            SegmentsToReturn = [new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = itemId }],
            DeleteSegmentException = expectedException
        };
        var libraryManager = EntrypointTestHelpers.CreateLibraryManager(CreateMovie(itemId));
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { MaxParallelism = 2 });
        var refresher = CreateRefresher(manager, libraryManager);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => refresher.RemoveIntroSkipperSegmentsAsync([itemId], CancellationToken.None));

        Assert.Same(expectedException, exception);
    }

    private static MediaSegmentRefreshService CreateRefresher(FakeMediaSegmentManager manager, ILibraryManager? libraryManager = null)
        => new(manager, libraryManager ?? EntrypointTestHelpers.CreateLibraryManager(), NullLogger<MediaSegmentRefreshService>.Instance);

    private static Movie CreateMovie(Guid itemId)
    {
        var item = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(item, "Id", itemId);
        EntrypointTestHelpers.EnsureNonVirtual(item);
        return item;
    }

    private sealed class FakeMediaSegmentManager : IMediaSegmentManager
    {
        public int RunCount { get; private set; }

        public Guid LastItemId { get; private set; }

        public bool? LastForceOverwrite { get; private set; }

        public LibraryOptions? LastLibraryOptions { get; private set; }

        public bool LastFilterByProvider { get; private set; }

        public List<Guid> DeletedSegmentIds { get; } = [];

        public Exception? RunException { get; init; }

        public Exception? DeleteSegmentException { get; init; }

        public IReadOnlyList<MediaSegmentDto> SegmentsToReturn { get; init; } = [];

        public TaskCompletionSource? RunCompletion { get; init; }

        public Task RunSegmentPluginProviders(BaseItem baseItem, LibraryOptions libraryOptions, bool forceOverwrite, CancellationToken cancellationToken)
        {
            RunCount++;
            LastItemId = baseItem.Id;
            LastForceOverwrite = forceOverwrite;
            LastLibraryOptions = libraryOptions;

            if (RunException is not null)
            {
                throw RunException;
            }

            return RunCompletion?.Task ?? Task.CompletedTask;
        }

        public bool IsTypeSupported(BaseItem baseItem) => true;

        public Task<MediaSegmentDto> CreateSegmentAsync(MediaSegmentDto mediaSegment, string segmentProviderId) => Task.FromResult(mediaSegment);

        public Task DeleteSegmentAsync(Guid segmentId)
        {
            if (DeleteSegmentException is not null)
            {
                throw DeleteSegmentException;
            }

            DeletedSegmentIds.Add(segmentId);
            return Task.CompletedTask;
        }

        public Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IEnumerable<MediaSegmentDto>> GetSegmentsAsync(BaseItem item, IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions libraryOptions, bool filterByProvider = true)
        {
            LastLibraryOptions = libraryOptions;
            LastFilterByProvider = filterByProvider;
            return Task.FromResult<IEnumerable<MediaSegmentDto>>(SegmentsToReturn);
        }

        public bool HasSegments(Guid itemId) => false;

        public IEnumerable<(string Name, string Id)> GetSupportedProviders(BaseItem item)
            =>
            [
                (Plugin.ProviderName, "intro-skipper"),
                ("Chapter Segments Provider", "chapter")
            ];
    }
}
