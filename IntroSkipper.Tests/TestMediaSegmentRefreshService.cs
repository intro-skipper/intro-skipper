using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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

    private static MediaSegmentRefreshService CreateRefresher(FakeMediaSegmentManager manager)
        => new(manager, NullLogger<MediaSegmentRefreshService>.Instance);

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

        public Exception? RunException { get; init; }

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

        public Task DeleteSegmentAsync(Guid segmentId) => Task.CompletedTask;

        public Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IEnumerable<MediaSegmentDto>> GetSegmentsAsync(BaseItem item, IEnumerable<MediaSegmentType>? typeFilter, LibraryOptions libraryOptions, bool filterByProvider = true)
        {
            return Task.FromResult<IEnumerable<MediaSegmentDto>>(Array.Empty<MediaSegmentDto>());
        }

        public bool HasSegments(Guid itemId) => false;

        public IEnumerable<(string Name, string Id)> GetSupportedProviders(BaseItem item) => [(Plugin.Instance!.Name, "intro-skipper")];
    }
}
