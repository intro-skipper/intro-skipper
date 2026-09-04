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
public sealed class TestCleanCacheTask : IDisposable
{
    private readonly TempSegmentDb _db = new();
    private readonly TempCacheDb _cache = new();

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
        var liveEpisodeId = Guid.NewGuid();
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { UpdateMediaSegments = true }, _cache.Path);

        var database = _db.Database;
        var cacheDatabase = _cache.CreateDatabase();
        await SeedAsync(database, cacheDatabase, liveEpisodeId);

        var progress = new RecordingProgress();
        var store = new FakeJellyfinSegmentStore();
        await CreateTask(CreateIncompleteLibrary(reason, liveEpisodeId), database, cacheDatabase, store).ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(100, progress.Value);
        Assert.Equal(0, store.WriteCallCount);
        await AssertSeededDataIntactAsync(database, cacheDatabase, liveEpisodeId);
    }

    [Fact]
    public async Task GetMediaInventoryAsync_ResetsEnumerationFailureCount_AcrossCalls()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());

        var calls = 0;
        var libraryManager = FakeLibraryManager.Create(
            [JellyfinItems.Folder("Movies")],
            _ => ++calls == 1 ? throw new InvalidOperationException("first pass fails") : []);
        var queueManager = new QueueManager(
            NullLogger<QueueManager>.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        await queueManager.GetMediaInventoryAsync(includeExcluded: true);
        Assert.Equal(1, queueManager.EnumerationFailureCount);

        await queueManager.GetMediaInventoryAsync(includeExcluded: true);
        Assert.Equal(0, queueManager.EnumerationFailureCount);
    }

    [Fact]
    public async Task ExecuteAsync_DeletesOnlyStaleRows_WhenEnumerationSucceeds()
    {
        var movieId = Guid.NewGuid();
        var staleEpisodeId = Guid.NewGuid();
        var config = new PluginConfiguration { UpdateMediaSegments = false };
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, _cache.Path);

        var database = _db.Database;
        var cacheDatabase = _cache.CreateDatabase();

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

        var libraryManager = FakeLibraryManager.Create([JellyfinItems.Folder("Movies")], _ => [JellyfinItems.Movie(movieId)]);

        var progress = new RecordingProgress();
        await CreateTask(libraryManager, database, cacheDatabase).ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(100, progress.Value);

        Assert.NotEmpty(await database.GetSegmentsAsync(movieId));
        Assert.Empty(await database.GetSegmentsAsync(staleEpisodeId));
        Assert.Empty(await cacheDatabase.GetStaleItemIdsAsync(new HashSet<Guid> { movieId }));
        Assert.NotNull(cacheDatabase.FindEntry(movieId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0));
        Assert.Null(cacheDatabase.FindEntry(movieId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));
        Assert.Null(cacheDatabase.FindEntry(staleEpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0));

        await using var db = _db.Context();
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

    [Fact]
    public async Task ExecuteAsync_RetainsRows_OfItemsInProviderDisabledLibraries()
    {
        var movieId = Guid.NewGuid();
        var disabledLibraryEpisodeId = Guid.NewGuid();
        var goneEpisodeId = Guid.NewGuid();
        var config = new PluginConfiguration { UpdateMediaSegments = false };
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, _cache.Path);

        var database = _db.Database;
        var cacheDatabase = _cache.CreateDatabase();
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
        var moviesFolder = JellyfinItems.Folder("Movies");
        var disabledFolder = JellyfinItems.Folder("Anime");
        disabledFolder.LibraryOptions = new LibraryOptions { DisabledMediaSegmentProviders = [Plugin.Instance!.Name] };
        var libraryManager = FakeLibraryManager.Create(
            [moviesFolder, disabledFolder],
            _ => [JellyfinItems.Movie(movieId)],
            getItemById: id => id == disabledLibraryEpisodeId ? JellyfinItems.Movie(id) : null);

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
        await using var db = _db.Context();
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
                var moviesFolder = JellyfinItems.Folder("Movies");
                return FakeLibraryManager.Create(
                    [moviesFolder, JellyfinItems.Folder("Shows")],
                    folderId => folderId == Guid.Parse(moviesFolder.ItemId!)
                        ? [JellyfinItems.Movie(Guid.NewGuid())]
                        : throw new InvalidOperationException("library database unavailable"));

            case IncompleteInventory.ItemFailsToQueue:
                // The live episode itself is the one that fails to queue (an empty
                // SeasonId needs the unavailable provider manager), so it must survive.
                var brokenEpisode = JellyfinItems.Episode(liveEpisodeId, Guid.NewGuid(), Guid.Empty, "Show", path: "/media/show/s01e01.mkv");
                return FakeLibraryManager.Create([JellyfinItems.Folder("Media")], _ => [brokenEpisode, JellyfinItems.Movie(Guid.NewGuid())]);

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

    private async Task AssertSeededDataIntactAsync(IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase, Guid episodeId)
    {
        Assert.NotEmpty(await database.GetSegmentsAsync(episodeId));
        Assert.NotNull(cacheDatabase.FindEntry(episodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0));

        await using var db = _db.Context();
        Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.SeasonStates, s => s.SeasonId == episodeId));
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public double? Value { get; private set; }

        public void Report(double value) => Value = value;
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
    }
}
