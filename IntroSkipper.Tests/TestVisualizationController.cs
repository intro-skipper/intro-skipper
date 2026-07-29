using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestVisualizationController
{
    [Fact]
    public async Task EraseSeasonAsync_AwaitsDirectMediaSegmentRefresh_BeforeReturningNoContent()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);
        await SeedSeasonAsync(dbPath, seasonId, episodeIds);
        var refresher = new RecordingMediaSegmentRefresher
        {
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var loggerFactory = LoggerFactory.Create(builder => { });
        // Pre-warm the facade's init gate so the action below runs synchronously up to the
        // refresher await (its single pending point). With a cold gate, initialization
        // completes on the thread pool and the pre-completion assertions race it. This
        // mirrors production, where the hosted initializer warms the gate before traffic.
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        await database.InitializeAsync();
        var controller = CreateController(refresher, loggerFactory, database, pluginScope.CacheDbPath);

        var actionTask = controller.EraseSeasonAsync(seriesId, seasonId, eraseCache: false, CancellationToken.None);

        Assert.False(actionTask.IsCompleted);
        Assert.Equal(episodeIds.OrderBy(id => id), refresher.LastItemIds.OrderBy(id => id));

        refresher.Completion.SetResult();
        var result = await actionTask;

        Assert.IsType<NoContentResult>(result);
        await using var db = new IntroSkipperDbContext(dbPath);

        // Covers the seeded tombstone as well: a season erase deletes suppressed rows too.
        Assert.False(await db.Segments.AnyAsync(s => episodeIds.Contains(s.ItemId)));
        var seasonStates = await db.SeasonStates.Where(s => s.SeasonId == seasonId).ToListAsync();
        Assert.All(seasonStates, state => Assert.Empty(state.EpisodeIds));
    }

    [Fact]
    public async Task EraseSeasonAsync_AlwaysDelegatesRefresh_TheServiceOwnsTheMirrorFlag()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: false);
        await SeedSeasonAsync(dbPath, seasonId, episodeIds);
        var refresher = new RecordingMediaSegmentRefresher();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(refresher, loggerFactory, dbPath, pluginScope.CacheDbPath);

        var result = await controller.EraseSeasonAsync(seriesId, seasonId, eraseCache: false, CancellationToken.None);

        // The controller no longer gates on UpdateMediaSegments: it always delegates,
        // and MediaSegmentRefreshService itself owns the mirror flag.
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, refresher.CollectionCallCount);
    }

    [Fact]
    public async Task EraseSeasonAsync_CacheFailureStillClearsMainDatabaseState()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: false);
        await SeedSeasonAsync(dbPath, seasonId, episodeIds);
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var missingCachePath = Path.Join(
            Path.GetTempPath(),
            "IntroSkipper.Tests",
            "visualization-controller",
            Guid.NewGuid().ToString("N"),
            "cache.db");
        var controller = CreateController(
            new RecordingMediaSegmentRefresher(),
            loggerFactory,
            DatabaseTestHelpers.CreateSegmentDatabase(dbPath),
            missingCachePath);

        var result = await controller.EraseSeasonAsync(
            seriesId,
            seasonId,
            eraseCache: true,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        await using var db = new IntroSkipperDbContext(dbPath);

        // Covers the seeded tombstone as well: a season erase deletes suppressed rows too.
        Assert.False(await db.Segments.AnyAsync(s => episodeIds.Contains(s.ItemId)));
        var seasonStates = await db.SeasonStates.Where(s => s.SeasonId == seasonId).ToListAsync();
        Assert.All(seasonStates, state => Assert.Empty(state.EpisodeIds));
    }

    [Fact]
    public async Task ClearExcludedTimestampsAsync_RemovesOnlyCurrentlyExcludedState()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var excludedId = Guid.NewGuid();
        var includedId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        var config = new PluginConfiguration
        {
            UpdateMediaSegments = true,
            SeriesExclusions = { "Excluded Show" }
        };
        using var pluginScope = CreatePluginScope(
            seriesId,
            seasonId,
            [excludedId, includedId],
            config);
        await SeedSeasonAsync(dbPath, seasonId, [excludedId, includedId]);
        await SeedCacheAsync(pluginScope.CacheDbPath, excludedId, includedId);
        var refresher = new RecordingMediaSegmentRefresher();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(refresher, loggerFactory, dbPath, pluginScope.CacheDbPath);

        var result = await controller.ClearExcludedTimestampsAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ClearExcludedTimestampsResponse>(ok.Value);
        Assert.Equal(1, response.AffectedItems);

        // The excluded episode's active segment plus its tombstone: clearing excluded
        // items is a full erase, so suppressed rows are deleted (and counted) too.
        Assert.Equal(2, response.RemovedSegments);
        Assert.Equal(1, response.RemovedCacheEntries);
        Assert.Equal([excludedId], refresher.LastItemIds);

        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.False(await db.Segments.AnyAsync(s => s.ItemId == excludedId));
        Assert.True(await db.Segments.AnyAsync(s => s.ItemId == includedId));
        var seasonStates = await db.SeasonStates.Where(s => s.SeasonId == seasonId).ToListAsync();
        Assert.All(seasonStates, state => Assert.Equal([includedId], state.EpisodeIds));

        using var cacheDb = new DetectionCacheDbContext(pluginScope.CacheDbPath);
        Assert.False(await cacheDb.DetectionCache.AnyAsync(e => e.ItemId == excludedId));
        Assert.True(await cacheDb.DetectionCache.AnyAsync(e => e.ItemId == includedId));
    }

    [Fact]
    public async Task ClearExcludedTimestampsAsync_CacheFailureStillCommitsMainDatabaseChanges()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var excludedId = Guid.NewGuid();
        var includedId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        var config = new PluginConfiguration
        {
            UpdateMediaSegments = false,
            SeriesExclusions = { "Excluded Show" }
        };
        using var pluginScope = CreatePluginScope(
            seriesId,
            seasonId,
            [excludedId, includedId],
            config);
        await SeedSeasonAsync(dbPath, seasonId, [excludedId, includedId]);
        var missingCachePath = Path.Join(
            Path.GetTempPath(),
            "IntroSkipper.Tests",
            "visualization-controller",
            Guid.NewGuid().ToString("N"),
            "cache.db");
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(
            new RecordingMediaSegmentRefresher(),
            loggerFactory,
            DatabaseTestHelpers.CreateSegmentDatabase(dbPath),
            missingCachePath);

        var result = await controller.ClearExcludedTimestampsAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ClearExcludedTimestampsResponse>(ok.Value);
        Assert.Equal(1, response.AffectedItems);

        // Active segment + tombstone of the excluded episode (see SeedSeasonAsync).
        Assert.Equal(2, response.RemovedSegments);
        Assert.Equal(0, response.RemovedCacheEntries);

        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.False(await db.Segments.AnyAsync(s => s.ItemId == excludedId));
        Assert.True(await db.Segments.AnyAsync(s => s.ItemId == includedId));
        var seasonStates = await db.SeasonStates.Where(s => s.SeasonId == seasonId).ToListAsync();
        Assert.All(seasonStates, state => Assert.Equal([includedId], state.EpisodeIds));
    }

    [Fact]
    public async Task DisabledItems_PutGetDelete_RoundTripsAndRefreshes()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(CreateEpisodeItem(episodeIds[0], seasonId)));
        var refresher = new RecordingMediaSegmentRefresher();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(refresher, loggerFactory, database, pluginScope.CacheDbPath);

        var putResult = await controller.DisableItem(seasonId, episodeIds[0], CancellationToken.None);

        Assert.IsType<NoContentResult>(putResult);
        Assert.Equal([episodeIds[0]], refresher.LastItemIds);
        Assert.True(await database.IsItemDisabledAsync(episodeIds[0]));

        var getResult = await controller.GetDisabledItems(seasonId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(getResult.Result);
        var ids = Assert.IsAssignableFrom<IReadOnlySet<Guid>>(ok.Value);
        Assert.Equal([episodeIds[0]], ids);

        var deleteResult = await controller.EnableItem(seasonId, episodeIds[0], CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResult);

        // Both directions resync the item's mirror.
        Assert.Equal(2, refresher.CollectionCallCount);
        Assert.False(await database.IsItemDisabledAsync(episodeIds[0]));
    }

    [Fact]
    public async Task DisabledItems_RejectSeasonMismatchAndUnknownItem()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(CreateEpisodeItem(episodeIds[0], seasonId)));
        var refresher = new RecordingMediaSegmentRefresher();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(refresher, loggerFactory, database, pluginScope.CacheDbPath);

        var mismatch = await controller.DisableItem(Guid.NewGuid(), episodeIds[0], CancellationToken.None);
        var unknown = await controller.DisableItem(seasonId, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(mismatch);
        Assert.IsType<NotFoundResult>(unknown);
        Assert.Equal(0, refresher.CollectionCallCount);
        Assert.False(await database.IsItemDisabledAsync(episodeIds[0]));
    }

    [Fact]
    public async Task DisabledItems_MovieUsesItsOwnIdAsSeasonKey()
    {
        var movieId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], updateMediaSegments: true);
        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", movieId);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(movie));
        var refresher = new RecordingMediaSegmentRefresher();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(refresher, loggerFactory, database, pluginScope.CacheDbPath);

        var result = await controller.DisableItem(movieId, movieId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.True(await database.IsItemDisabledAsync(movieId));

        var mismatch = await controller.DisableItem(Guid.NewGuid(), movieId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(mismatch);
    }

    private static Episode CreateEpisodeItem(Guid episodeId, Guid seasonId)
    {
        var episode = new Episode();
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeasonId", seasonId);
        return episode;
    }

    private static VisualizationController CreateController(RecordingMediaSegmentRefresher refresher, ILoggerFactory loggerFactory, string dbPath, string cacheDbPath)
        => CreateController(refresher, loggerFactory, DatabaseTestHelpers.CreateSegmentDatabase(dbPath), cacheDbPath);

    private static VisualizationController CreateController(RecordingMediaSegmentRefresher refresher, ILoggerFactory loggerFactory, IntroSkipperDatabase database, string cacheDbPath)
    {
        return new VisualizationController(
            NullLogger<VisualizationController>.Instance,
            refresher,
            libraryManager: null!,
            new AnalyzerTaskFactory(
                loggerFactory,
                libraryManager: null!,
                providerManager: null!,
                fileSystem: null!,
                mediaSegmentRefresher: null!,
                ffmpegService: null!,
                DatabaseTestHelpers.CreateCacheService(cacheDbPath),
                database),
            database,
            DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath));
    }

    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope(Guid seriesId, Guid seasonId, IReadOnlyList<Guid> episodeIds, bool updateMediaSegments)
        => CreatePluginScope(
            seriesId,
            seasonId,
            episodeIds,
            new PluginConfiguration { UpdateMediaSegments = updateMediaSegments });

    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope(Guid seriesId, Guid seasonId, IReadOnlyList<Guid> episodeIds, PluginConfiguration config)
    {
        var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", config);
        EntrypointTestHelpers.SetPropertyOrField(plugin, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());
        plugin.QueuedMediaItems[seasonId] =
        [
            new QueuedEpisode { SeriesId = seriesId, SeasonId = seasonId, EpisodeId = episodeIds[0], SeriesName = "Excluded Show", Name = "Episode 1" },
            new QueuedEpisode { SeriesId = seriesId, SeasonId = seasonId, EpisodeId = episodeIds[1], SeriesName = "Included Show", Name = "Episode 2" }
        ];
        return scope;
    }

    private static async Task SeedSeasonAsync(string dbPath, Guid seasonId, IReadOnlyList<Guid> episodeIds)
    {
        await using var db = new IntroSkipperDbContext(dbPath);
        await db.ApplyMigrationsAsync();
        db.Segments.AddRange(
            new DbSegment(episodeIds[0], AnalysisMode.Introduction, TickConversions.FromSeconds(10), TickConversions.FromSeconds(20), SegmentSource.Chapter),
            new DbSegment(episodeIds[1], AnalysisMode.Introduction, TickConversions.FromSeconds(30), TickConversions.FromSeconds(40), SegmentSource.Chapter),
            // Tombstone (user-deleted automatic segment) on the first episode: season erase and
            // clear-excluded are full erases, so they must delete suppressed rows too.
            new DbSegment(episodeIds[0], AnalysisMode.Introduction, TickConversions.FromSeconds(50), TickConversions.FromSeconds(60), SegmentSource.Chapter)
            {
                State = SegmentState.Suppressed,
            });
        db.SeasonStates.AddRange(
            new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default, episodeIds),
            new DbSeasonState(seasonId, AnalysisMode.Credits, AnalyzerAction.Default, episodeIds));
        await db.SaveChangesAsync();
    }

    private static async Task SeedCacheAsync(string cacheDbPath, Guid excludedId, Guid includedId)
    {
        using var cacheDb = new DetectionCacheDbContext(cacheDbPath);
        cacheDb.DetectionCache.AddRange(
            new DbDetectionCache(excludedId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray),
            new DbDetectionCache(includedId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
        await cacheDb.SaveChangesAsync();
    }

    private static string CreateTempDbPath()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "visualization-controller");
        Directory.CreateDirectory(tempDir);
        return Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");
    }

    private sealed class RecordingMediaSegmentRefresher : IMediaSegmentRefresher
    {
        public TaskCompletionSource? Completion { get; init; }

        public int CollectionCallCount { get; private set; }

        public IReadOnlyList<Guid> LastItemIds { get; private set; } = [];

        public Task RefreshAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            LastItemIds = [item.Id];
            return Completion?.Task ?? Task.CompletedTask;
        }

        public Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
        {
            CollectionCallCount++;
            LastItemIds = [.. itemIds];
            return Completion?.Task ?? Task.CompletedTask;
        }

        public Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
            => RefreshAsync(itemIds, cancellationToken);
    }
}
