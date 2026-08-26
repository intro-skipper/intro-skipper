// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

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
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for <see cref="CleanCacheTask"/>. The cleanup task deletes every row that is NOT in
/// the enumerated library queue, so these tests pin the guards that keep an incomplete or
/// empty enumeration from mass-deleting healthy data.
/// </summary>
public sealed class TestCleanCacheTask
{
    [Fact]
    public async Task ExecuteAsync_SkipsAllCleanup_WhenALibraryFailsToEnumerate()
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var liveEpisodeId = Guid.NewGuid();
        var liveMovieId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { UpdateMediaSegments = true });

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
            await SeedAsync(database, cacheDatabase, liveEpisodeId);

            // One library enumerates fine (non-empty queue) while a second one throws, so the
            // failure guard is exercised independently of the empty-queue guard.
            var moviesFolder = NewFolder("Movies");
            var showsFolder = NewFolder("Shows");
            var libraryManager = FakeLibraryManager.Create(
                [moviesFolder, showsFolder],
                folderId => folderId == Guid.Parse(moviesFolder.ItemId!)
                    ? [CreateMovie(liveMovieId)]
                    : throw new InvalidOperationException("library database unavailable"));

            var progress = new RecordingProgress();
            var refresher = new RecordingRefresher();
            await CreateTask(libraryManager, database, cacheDatabase, refresher).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);
            Assert.Equal(0, refresher.RemoveCallCount);
            await AssertSeededDataIntactAsync(database, cacheDatabase, liveEpisodeId, dbPath);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SkipsAllCleanup_WhenAnItemFailsToQueue()
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var brokenEpisodeId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { UpdateMediaSegments = false });

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
            await SeedAsync(database, cacheDatabase, brokenEpisodeId);

            // The episode's queueing throws (its SeasonId repair needs the provider manager,
            // which is unavailable), while the sibling movie queues fine — so the queue is
            // non-empty but incomplete, and the broken live episode must not be deleted.
            var brokenEpisode = new Episode
            {
                SeriesId = Guid.NewGuid(),
                SeasonId = Guid.Empty,
                ParentIndexNumber = 1,
                IndexNumber = 1,
                Path = "/media/show/s01e01.mkv",
            };
            EntrypointTestHelpers.SetPropertyOrField(brokenEpisode, "Id", brokenEpisodeId);
            EntrypointTestHelpers.SetPropertyOrField(brokenEpisode, "SeriesName", "Show");
            EntrypointTestHelpers.EnsureNonVirtual(brokenEpisode);

            var libraryManager = FakeLibraryManager.Create(
                [NewFolder("Media")],
                _ => [brokenEpisode, CreateMovie(Guid.NewGuid())]);

            var progress = new RecordingProgress();
            await CreateTask(libraryManager, database, cacheDatabase).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);
            await AssertSeededDataIntactAsync(database, cacheDatabase, brokenEpisodeId, dbPath);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public async Task GetMediaItems_ResetsEnumerationFailureCount_AcrossCalls()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());

        var calls = 0;
        var libraryManager = FakeLibraryManager.Create(
            [NewFolder("Movies")],
            _ => ++calls == 1 ? throw new InvalidOperationException("first pass fails") : []);
        var queueManager = new QueueManager(
            NullLogger<QueueManager>.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        await queueManager.GetMediaItems(includeExcluded: true);
        Assert.Equal(1, queueManager.EnumerationFailureCount);

        await queueManager.GetMediaItems(includeExcluded: true);
        Assert.Equal(0, queueManager.EnumerationFailureCount);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsAllCleanup_WhenNoEnabledLibrariesHaveItems()
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var liveEpisodeId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { UpdateMediaSegments = false });

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
            await SeedAsync(database, cacheDatabase, liveEpisodeId);

            // No virtual folders at all: an empty queue must not classify everything as stale.
            var libraryManager = FakeLibraryManager.Create([], _ => []);

            var progress = new RecordingProgress();
            await CreateTask(libraryManager, database, cacheDatabase).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);
            await AssertSeededDataIntactAsync(database, cacheDatabase, liveEpisodeId, dbPath);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeletesOnlyStaleRows_WhenEnumerationSucceeds()
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var movieId = Guid.NewGuid();
        var staleEpisodeId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            var config = new PluginConfiguration { UpdateMediaSegments = false };
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", config);

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);

            // Live movie data plus rows for an item that is no longer in any library. The live
            // movie's readable cache row carries the current config hash; a second row with a
            // hash no read path accepts must be cleaned even though its item is still live.
            var currentHash = ConfigHasher.DetectionCache(config, CacheEntryType.Chromaprint, AnalysisMode.Introduction);
            await database.ReplaceAutoSegmentsAsync(
                movieId,
                AnalysisMode.Introduction,
                [new Segment(movieId, new TimeRange(10, 40))],
                SegmentSource.Chapter);
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [movieId], "hash");
            await database.SetAnalyzerActionAsync(movieId, new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Default });
            cacheDatabase.Upsert(movieId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0, EntrypointTestHelpers.EmptyJsonArray, currentHash);
            cacheDatabase.Upsert(movieId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30, EntrypointTestHelpers.EmptyJsonArray, "orphaned-hash");
            await SeedAsync(database, cacheDatabase, staleEpisodeId);

            // Disabled flags follow the item: the live movie's flag carries a stale
            // season key (drift) and must survive; the stale episode's flag must go.
            await database.SetItemDisabledAsync(Guid.NewGuid(), movieId, disabled: true);
            await database.SetItemDisabledAsync(staleEpisodeId, staleEpisodeId, disabled: true);

            var libraryManager = FakeLibraryManager.Create([NewFolder("Movies")], _ => [CreateMovie(movieId)]);

            var progress = new RecordingProgress();
            await CreateTask(libraryManager, database, cacheDatabase).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);

            Assert.NotEmpty(await database.GetSegmentsAsync(movieId));
            Assert.Empty(await database.GetSegmentsAsync(staleEpisodeId));
            Assert.Empty(await cacheDatabase.GetStaleItemIdsAsync(new HashSet<Guid> { movieId }));
            Assert.NotNull(cacheDatabase.FindEntry(movieId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0));
            Assert.Null(cacheDatabase.FindEntry(movieId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));
            Assert.Null(cacheDatabase.FindEntry(staleEpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0));

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.SeasonStates, s => s.SeasonId == movieId));
            Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.SeasonStates, s => s.SeasonId == staleEpisodeId));
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.DisabledItems, e => e.ItemId == movieId));
            Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.DisabledItems, e => e.ItemId == staleEpisodeId));
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.AnalyzedItems, a => a.ItemId == movieId));
            Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.AnalyzedItems, a => a.ItemId == staleEpisodeId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    private static CleanCacheTask CreateTask(
        ILibraryManager libraryManager,
        IIntroSkipperDatabase database,
        IDetectionCacheDatabase cacheDatabase,
        IMediaSegmentRefresher? mediaSegmentRefresher = null)
        => new(
            NullLogger<CleanCacheTask>.Instance,
            new AnalyzerTaskFactory(
                NullLoggerFactory.Instance,
                libraryManager,
                providerManager: null!,
                fileSystem: null!,
                mediaSegmentRefresher: null!,
                ffmpegService: null!,
                cacheService: null!,
                database),
            database,
            cacheDatabase,
            new DetectionCacheService(NullLogger<DetectionCacheService>.Instance, cacheDatabase),
            mediaSegmentRefresher: mediaSegmentRefresher ?? new RecordingRefresher());

    private static MediaBrowser.Controller.Entities.Movies.Movie CreateMovie(Guid id)
        => new()
        {
            Id = id,
            Name = "Test Movie",
            Path = "/media/test-movie.mkv",
        };

    private static async Task SeedAsync(IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase, Guid episodeId)
    {
        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Introduction,
            [new Segment(episodeId, new TimeRange(5, 65))],
            SegmentSource.Chapter);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [episodeId], "hash");
        await database.SetAnalyzerActionAsync(episodeId, new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Default });
        cacheDatabase.Upsert(episodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0, EntrypointTestHelpers.EmptyJsonArray, "hash");
    }

    private static async Task AssertSeededDataIntactAsync(IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase, Guid episodeId, string dbPath)
    {
        Assert.NotEmpty(await database.GetSegmentsAsync(episodeId));
        Assert.NotNull(cacheDatabase.FindEntry(episodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0));

        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.SeasonStates, s => s.SeasonId == episodeId));
    }

    private static VirtualFolderInfo NewFolder(string name)
        => new()
        {
            Name = name,
            ItemId = Guid.NewGuid().ToString(),
        };

    private static (string DbPath, string CacheDbPath) CreateTempDbPaths()
        => (DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-cleantask.db"),
            DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-cleantask-cache.db"));

    private static void DeleteSqliteFiles(string dbPath)
        => DatabaseTestHelpers.DeleteSqliteFiles(dbPath);

    private sealed class RecordingProgress : IProgress<double>
    {
        public double? Value { get; private set; }

        public void Report(double value) => Value = value;
    }

    private sealed class RecordingRefresher : IMediaSegmentRefresher
    {
        public int RemoveCallCount { get; private set; }

        public Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
        {
            RemoveCallCount++;
            return Task.CompletedTask;
        }
    }

    // ILibraryManager stub for the queue-building path: returns the configured virtual
    // folders and delegates GetItemList to the configured behavior (which may throw to
    // simulate a failed library enumeration).
    private class FakeLibraryManager : DispatchProxy
    {
        private List<VirtualFolderInfo> _folders = [];
        private Func<Guid, List<BaseItem>> _getItemList = _ => [];

        public static ILibraryManager Create(List<VirtualFolderInfo> folders, Func<Guid, List<BaseItem>> getItemList)
        {
            var proxy = Create<ILibraryManager, FakeLibraryManager>();
            var fake = (FakeLibraryManager)(object)proxy;
            fake._folders = folders;
            fake._getItemList = getItemList;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(ILibraryManager.GetVirtualFolders) => _folders,
                nameof(ILibraryManager.GetItemList) => _getItemList(ExtractParentId(args)).ToList(),
                nameof(ILibraryManager.GetItemById) => null,
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
        }

        private static Guid ExtractParentId(object?[]? args)
            => args?.OfType<InternalItemsQuery>().FirstOrDefault()?.ParentId ?? Guid.Empty;
    }
}
