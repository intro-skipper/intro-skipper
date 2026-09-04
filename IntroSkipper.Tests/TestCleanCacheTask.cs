// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FakeLibraryManager = EntrypointTestHelpers.FakeLibraryManager;

/// <summary>
/// Tests for <see cref="CleanCacheTask"/>. The cleanup task deletes the rows of ids that are
/// absent from the enumerated library queue AND no longer resolve on the server, so these
/// tests pin the guards that keep an incomplete or empty enumeration, including a library
/// whose media segment provider is disabled, from mass-deleting healthy data.
/// </summary>
public sealed class TestCleanCacheTask
{
    /// <summary>
    /// Ways the library enumeration can come back incomplete or empty.
    /// </summary>
    public enum IncompleteInventory
    {
        /// <summary>One library enumerates fine (non-empty queue) while a second one throws.</summary>
        LibraryThrows,

        /// <summary>An episode fails to queue (its SeasonId repair needs the unavailable provider manager) beside a movie that queues fine.</summary>
        ItemFailsToQueue,

        /// <summary>No virtual folders at all: an empty queue must not classify everything as stale.</summary>
        NoLibraries,
    }

    [Theory]
    [InlineData(IncompleteInventory.LibraryThrows)]
    [InlineData(IncompleteInventory.ItemFailsToQueue)]
    [InlineData(IncompleteInventory.NoLibraries)]
    public async Task ExecuteAsync_SkipsAllCleanup_WhenTheInventoryIsIncomplete(IncompleteInventory reason)
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var liveEpisodeId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { UpdateMediaSegments = true });

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
            await SeedAsync(database, cacheDatabase, liveEpisodeId);

            var progress = new RecordingProgress();
            var store = new FakeJellyfinSegmentStore();
            await CreateTask(CreateIncompleteLibrary(reason, liveEpisodeId), database, cacheDatabase, store).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);
            Assert.Equal(0, store.WriteCallCount);
            await AssertSeededDataIntactAsync(database, cacheDatabase, liveEpisodeId, dbPath);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
            DatabaseTestHelpers.DeleteSqliteFiles(cacheDbPath);
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
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
            DatabaseTestHelpers.DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RetainsRows_OfItemsInProviderDisabledLibraries()
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var movieId = Guid.NewGuid();
        var disabledLibraryEpisodeId = Guid.NewGuid();
        var goneEpisodeId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            var config = new PluginConfiguration { UpdateMediaSegments = false };
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", config);

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
            await SeedAsync(database, cacheDatabase, disabledLibraryEpisodeId);
            await SeedAsync(database, cacheDatabase, goneEpisodeId);
            await database.SetItemDisabledAsync(disabledLibraryEpisodeId, disabledLibraryEpisodeId, disabled: true);

            // Rows carrying the current config hash survive the final unreadable-hash
            // sweep, so their fate isolates the item-based cache cleanup under test.
            var currentHash = ConfigHasher.DetectionCache(config, CacheEntryType.Chromaprint, AnalysisMode.Introduction);
            cacheDatabase.Upsert(disabledLibraryEpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30, EntrypointTestHelpers.EmptyJsonArray, currentHash);
            cacheDatabase.Upsert(goneEpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30, EntrypointTestHelpers.EmptyJsonArray, currentHash);

            // One enabled library with a live movie; a second library has the plugin's
            // provider disabled, so the queue never enumerates its episode. The episode
            // still resolves on the server, so all its rows must survive the reversible
            // toggle, while the id the server no longer knows is cleaned everywhere.
            var moviesFolder = NewFolder("Movies");
            var disabledFolder = NewFolder("Anime");
            disabledFolder.LibraryOptions = new LibraryOptions { DisabledMediaSegmentProviders = [Plugin.Instance!.Name] };
            var libraryManager = FakeLibraryManager.Create(
                [moviesFolder, disabledFolder],
                _ => [CreateMovie(movieId)],
                getItemById: id => id == disabledLibraryEpisodeId ? CreateMovie(id) : null);

            var progress = new RecordingProgress();
            var store = new FakeJellyfinSegmentStore();
            await CreateTask(libraryManager, database, cacheDatabase, store).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);

            // Mirroring is off in this configuration: the erase committed and journaled
            // the gone item's projection, which sits durably until mirroring turns on.
            Assert.Equal(0, store.WriteCallCount);
            Assert.NotNull(cacheDatabase.FindEntry(disabledLibraryEpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));
            Assert.Null(cacheDatabase.FindEntry(goneEpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));

            // The facade's servable read filters disabled items, so assert row survival
            // against the raw context.
            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.Segments, s => s.ItemId == disabledLibraryEpisodeId));
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.SeasonStates, s => s.SeasonId == disabledLibraryEpisodeId));
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.AnalyzedItems, a => a.ItemId == disabledLibraryEpisodeId));
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.DisabledItems, e => e.ItemId == disabledLibraryEpisodeId));
            Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.Segments, s => s.ItemId == goneEpisodeId));
            Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.SeasonStates, s => s.SeasonId == goneEpisodeId));
            Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.AnalyzedItems, a => a.ItemId == goneEpisodeId));
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.ProjectionQueue, q => q.ItemId == goneEpisodeId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
            DatabaseTestHelpers.DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public void DetectionCacheHash_CoversEveryCacheEntryTypeAndMode()
    {
        var config = new PluginConfiguration();

        // DeleteUnreadableEntriesAsync enumerates every (mode, type) pair through
        // ConfigHasher.DetectionCache, whose switch throws on an unmapped CacheEntryType
        // (the compiler cannot flag the gap). This fails at the moment a member is added
        // without its switch arm, instead of faulting every cache-cleanup run at runtime.
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            foreach (var type in Enum.GetValues<CacheEntryType>())
            {
                Assert.False(string.IsNullOrEmpty(ConfigHasher.DetectionCache(config, type, mode)));
            }
        }
    }

    private static ILibraryManager CreateIncompleteLibrary(IncompleteInventory reason, Guid liveEpisodeId)
    {
        switch (reason)
        {
            case IncompleteInventory.LibraryThrows:
                var moviesFolder = NewFolder("Movies");
                return FakeLibraryManager.Create(
                    [moviesFolder, NewFolder("Shows")],
                    folderId => folderId == Guid.Parse(moviesFolder.ItemId!)
                        ? [CreateMovie(Guid.NewGuid())]
                        : throw new InvalidOperationException("library database unavailable"));

            case IncompleteInventory.ItemFailsToQueue:
                // The live episode itself is the one that fails to queue, so it must survive.
                var brokenEpisode = new Episode
                {
                    SeriesId = Guid.NewGuid(),
                    SeasonId = Guid.Empty,
                    ParentIndexNumber = 1,
                    IndexNumber = 1,
                    Path = "/media/show/s01e01.mkv",
                };
                EntrypointTestHelpers.SetPropertyOrField(brokenEpisode, "Id", liveEpisodeId);
                EntrypointTestHelpers.SetPropertyOrField(brokenEpisode, "SeriesName", "Show");
                EntrypointTestHelpers.EnsureNonVirtual(brokenEpisode);
                return FakeLibraryManager.Create([NewFolder("Media")], _ => [brokenEpisode, CreateMovie(Guid.NewGuid())]);

            default:
                return FakeLibraryManager.Create([], _ => []);
        }
    }

    private static CleanCacheTask CreateTask(
        ILibraryManager libraryManager,
        IntroSkipperDatabase database,
        IDetectionCacheDatabase cacheDatabase,
        FakeJellyfinSegmentStore? store = null)
        => new(
            NullLogger<CleanCacheTask>.Instance,
            new AnalyzerTaskFactory(
                NullLoggerFactory.Instance,
                libraryManager,
                providerManager: null!,
                fileSystem: null!,
                ffmpegService: null!,
                cacheService: null!,
                database),
            libraryManager,
            database,
            cacheDatabase,
            new DetectionCacheService(NullLogger<DetectionCacheService>.Instance, cacheDatabase),
            DatabaseTestHelpers.CreateSegmentChange(store ?? new FakeJellyfinSegmentStore(), database));

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

    private sealed class RecordingProgress : IProgress<double>
    {
        public double? Value { get; private set; }

        public void Report(double value) => Value = value;
    }
}
