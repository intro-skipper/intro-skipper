namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestBaseItemAnalyzerTaskOrchestration
{
    [Fact]
    public async Task AnalyzeItemsAsync_PropagatesCancellationToFfmpegValidation()
    {
        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var analyzer = new AnalyzerTaskFactory(
            NullLoggerFactory.Instance,
            EntrypointTestHelpers.CreateLibraryManager(),
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: new FFmpegService(
                NullLogger<FFmpegService>.Instance,
                DatabaseTestHelpers.CreateTempCacheService()),
            cacheService: null!,
            database: null!).CreateAnalyzerTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeItemsAsync(new Progress<double>(), cancellation.Token));
    }

    [Fact]
    public async Task AnalyzeItemsAsync_InvalidFfmpeg_IsProbedOnceAcrossAnalyzerAndQueueVerification()
    {
        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());

        var episode = new Episode
        {
            Name = "Episode",
            SeriesId = Guid.NewGuid(),
            SeasonId = Guid.NewGuid(),
            ParentIndexNumber = 1,
            IndexNumber = 1,
            Path = "/media/missing-episode.mkv",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", Guid.NewGuid());
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeriesName", "Series");
        EntrypointTestHelpers.EnsureNonVirtual(episode);

        var libraryManager = QueueLibraryManager.Create(episode);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", libraryManager);
        var ffmpegService = CountingFfmpegService.Create(out var ffmpegProxy);
        var analyzer = new AnalyzerTaskFactory(
            NullLoggerFactory.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            ffmpegService,
            cacheService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase()).CreateAnalyzerTask();

        await analyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(1, ffmpegProxy.VersionCheckCount);
    }

    private class CountingFfmpegService : DispatchProxy
    {
        private int _versionCheckCount;

        public int VersionCheckCount => Volatile.Read(ref _versionCheckCount);

        public static IFFmpegService Create(out CountingFfmpegService proxy)
        {
            var service = Create<IFFmpegService, CountingFfmpegService>();
            proxy = (CountingFfmpegService)(object)service;
            return service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IFFmpegService.CheckFFmpegVersionAsync))
            {
                Interlocked.Increment(ref _versionCheckCount);
                return Task.FromResult(false);
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    private class QueueLibraryManager : DispatchProxy
    {
        private Episode _episode = null!;

        public static ILibraryManager Create(Episode episode)
        {
            var libraryManager = Create<ILibraryManager, QueueLibraryManager>();
            ((QueueLibraryManager)(object)libraryManager)._episode = episode;
            return libraryManager;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(ILibraryManager.GetVirtualFolders) => new List<VirtualFolderInfo>
                {
                    new()
                    {
                        Name = "Media",
                        ItemId = Guid.NewGuid().ToString(),
                    },
                },
                nameof(ILibraryManager.GetItemList) => new List<BaseItem> { _episode },
                nameof(ILibraryManager.GetItemById) => null,
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
        }
    }
}
