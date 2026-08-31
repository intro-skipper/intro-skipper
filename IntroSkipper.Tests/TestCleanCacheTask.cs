// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestCleanCacheTask
{
    [Fact]
    public async Task ExecuteAsync_IncompleteInventory_PreservesStoredData()
    {
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var scope = CreatePluginScope(dbPath);
        await SeedStoredDataAsync(dbPath, seasonId, episodeId);
        var task = CreateTask(EntrypointTestHelpers.CreateLibraryManager());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.True(await db.DbSegment.AnyAsync(s => s.ItemId == episodeId));
        Assert.True(await db.DbSeasonState.AnyAsync(s => s.SeasonId == seasonId));
        using var cacheDb = Plugin.CreateCacheDbContext();
        Assert.True(await cacheDb.DetectionCache.AnyAsync(e => e.ItemId == episodeId));
    }

    [Fact]
    public async Task ExecuteAsync_CompleteInventory_RemovesStoredDataForMissingItems()
    {
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var scope = CreatePluginScope(dbPath);
        await SeedStoredDataAsync(dbPath, seasonId, episodeId);
        var libraryManager = InventoryLibraryManager.Create([], []);
        var progress = new RecordingProgress();
        var task = CreateTask(libraryManager);

        await task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(100, progress.Value);
        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.False(await db.DbSegment.AnyAsync());
        Assert.False(await db.DbSeasonState.AnyAsync());
        using var cacheDb = Plugin.CreateCacheDbContext();
        Assert.False(await cacheDb.DetectionCache.AnyAsync());
    }

    [Fact]
    public async Task GetMediaInventory_LibraryQueryFailure_IsIncomplete()
    {
        using var scope = CreatePluginScope(CreateTempDbPath());
        var libraryManager = InventoryLibraryManager.Create(
            [CreateVirtualFolder()],
            [],
            throwOnItemList: true);
        var queueManager = CreateQueueManager(libraryManager);

        var inventory = await queueManager.GetMediaInventory(includeExcluded: true);

        Assert.False(inventory.IsComplete);
    }

    [Fact]
    public async Task GetMediaInventory_InvalidFolderItemId_IsIncomplete()
    {
        using var scope = CreatePluginScope(CreateTempDbPath());
        var folder = new VirtualFolderInfo
        {
            Name = "Library",
            ItemId = "not-a-guid",
        };
        var libraryManager = InventoryLibraryManager.Create([folder], []);
        var queueManager = CreateQueueManager(libraryManager);

        var inventory = await queueManager.GetMediaInventory(includeExcluded: true);

        Assert.False(inventory.IsComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMediaInventory_OmittedSupportedItem_IsIncomplete(bool throwDuringProbe)
    {
        using var scope = CreatePluginScope(CreateTempDbPath(), probeAudioDuration: throwDuringProbe);
        var movie = new Movie
        {
            Name = "Movie",
            Path = throwDuringProbe ? "/tmp/movie.mkv" : string.Empty,
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
        };
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", Guid.NewGuid());
        var libraryManager = InventoryLibraryManager.Create([CreateVirtualFolder()], [movie]);
        var queueManager = CreateQueueManager(libraryManager);

        var inventory = await queueManager.GetMediaInventory(includeExcluded: true);

        Assert.False(inventory.IsComplete);
    }

    private static CleanCacheTask CreateTask(ILibraryManager libraryManager)
        => new(
            NullLogger<CleanCacheTask>.Instance,
            NullLoggerFactory.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            new ProbeFailureFfmpegService(),
            new NullMediaSegmentRefresher());

    private static QueueManager CreateQueueManager(ILibraryManager libraryManager)
        => new(
            NullLogger<QueueManager>.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            new ProbeFailureFfmpegService());

    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope(
        string dbPath,
        bool probeAudioDuration = false)
    {
        var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "_dbPath", dbPath);
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration
        {
            ProbeAudioDuration = probeAudioDuration,
            UpdateMediaSegments = false,
        });
        return scope;
    }

    private static async Task SeedStoredDataAsync(string dbPath, Guid seasonId, Guid episodeId)
    {
        await using (var db = new IntroSkipperDbContext(dbPath))
        {
            await db.Database.EnsureCreatedAsync();
            db.DbSegment.Add(new DbSegment(
                new Segment(episodeId, new TimeRange(10, 20)),
                AnalysisMode.Introduction));
            db.DbSeasonState.Add(new DbSeasonState(
                seasonId,
                AnalysisMode.Introduction,
                AnalyzerAction.Default,
                [episodeId]));
            await db.SaveChangesAsync();
        }

        using var cacheDb = Plugin.CreateCacheDbContext();
        cacheDb.DetectionCache.Add(new DbDetectionCache(
            episodeId,
            AnalysisMode.Introduction,
            CacheEntryType.Chromaprint,
            EntrypointTestHelpers.EmptyJsonArray));
        await cacheDb.SaveChangesAsync();
    }

    private static VirtualFolderInfo CreateVirtualFolder()
        => new()
        {
            Name = "Library",
            ItemId = Guid.NewGuid().ToString("N"),
        };

    private static string CreateTempDbPath()
    {
        var directory = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "clean-cache");
        Directory.CreateDirectory(directory);
        return Path.Join(directory, Guid.NewGuid().ToString("N") + ".db");
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public double? Value { get; private set; }

        public void Report(double value) => Value = value;
    }

    private class InventoryLibraryManager : DispatchProxy
    {
        private IReadOnlyList<VirtualFolderInfo> _folders = [];
        private IReadOnlyList<BaseItem> _items = [];
        private bool _throwOnItemList;

        public static ILibraryManager Create(
            IReadOnlyList<VirtualFolderInfo> folders,
            IReadOnlyList<BaseItem> items,
            bool throwOnItemList = false)
        {
            var proxy = Create<ILibraryManager, InventoryLibraryManager>();
            var state = (InventoryLibraryManager)(object)proxy;
            state._folders = folders;
            state._items = items;
            state._throwOnItemList = throwOnItemList;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(ILibraryManager.GetVirtualFolders) => (List<VirtualFolderInfo>)[.. _folders],
                nameof(ILibraryManager.GetItemList) when _throwOnItemList => throw new IOException("library unavailable"),
                nameof(ILibraryManager.GetItemList) => (List<BaseItem>)[.. _items],
                nameof(ILibraryManager.GetItemById) => null,
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
        }
    }

    private sealed class ProbeFailureFfmpegService : IFFmpegService
    {
        public Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<uint>());

        public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<TimeRange>());

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, TimeRange range, int minimum, int threshold, AnalysisMode mode, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<BlackFrame>());

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<BlackFrame>());

        public Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<KeyframeVisual>());

        public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<BlackInterval>());

        public Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<double>());

        public Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
            => throw new IOException("probe failed");

        public string GetChromaprintLogs() => string.Empty;

        public FFmpegCheckResult GetCheckResult() => FFmpegCheckResult.NotRun;
    }

    private sealed class NullMediaSegmentRefresher : IMediaSegmentRefresher
    {
        public Task RefreshAsync(BaseItem item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
