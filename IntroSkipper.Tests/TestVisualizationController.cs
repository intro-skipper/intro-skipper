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
using MediaBrowser.Controller.Library;
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
        using var pluginScope = CreatePluginScope(dbPath, seriesId, seasonId, episodeIds, updateMediaSegments: true);
        await SeedSeasonAsync(dbPath, seasonId, episodeIds);
        var refresher = new RecordingMediaSegmentRefresher
        {
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(refresher, loggerFactory);

        var actionTask = controller.EraseSeasonAsync(seriesId, seasonId, eraseCache: false, CancellationToken.None);

        Assert.False(actionTask.IsCompleted);
        Assert.Equal(episodeIds.OrderBy(id => id), refresher.LastItemIds.OrderBy(id => id));

        refresher.Completion.SetResult();
        var result = await actionTask;

        Assert.IsType<NoContentResult>(result);
        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.False(await db.DbSegment.AnyAsync(s => episodeIds.Contains(s.ItemId)));
        var seasonStates = await db.DbSeasonState.Where(s => s.SeasonId == seasonId).ToListAsync();
        Assert.All(seasonStates, state => Assert.Empty(state.EpisodeIds));
    }

    [Fact]
    public async Task EraseSeasonAsync_DoesNotRefresh_WhenUpdateMediaSegmentsDisabled()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(dbPath, seriesId, seasonId, episodeIds, updateMediaSegments: false);
        await SeedSeasonAsync(dbPath, seasonId, episodeIds);
        var refresher = new RecordingMediaSegmentRefresher
        {
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(refresher, loggerFactory);

        var result = await controller.EraseSeasonAsync(seriesId, seasonId, eraseCache: false, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, refresher.CollectionCallCount);
    }

    [Fact]
    public async Task ClearExcludedTimestampsAsync_RemovesExcludedSeriesAndMovieStateAndRefreshesJellyfin()
    {
        var excludedEpisodeId = Guid.NewGuid();
        var excludedMovieId = Guid.NewGuid();
        var includedItemId = Guid.NewGuid();
        var stateOnlyExcludedId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "_dbPath", dbPath);
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration
        {
            ExcludeSeries = "The Office",
            ExcludeMovies = "The Matrix",
            UpdateMediaSegments = false
        });
        EntrypointTestHelpers.SetPropertyOrField(plugin, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());

        await using (var db = new IntroSkipperDbContext(dbPath))
        {
            await db.Database.EnsureCreatedAsync();
            db.DbSegment.AddRange(
                new DbSegment(new Segment(excludedEpisodeId, new TimeRange(10, 20)), AnalysisMode.Introduction, isUserProvided: true),
                new DbSegment(new Segment(excludedMovieId, new TimeRange(30, 40)), AnalysisMode.Credits),
                new DbSegment(new Segment(includedItemId, new TimeRange(50, 60)), AnalysisMode.Introduction));
            db.DbSeasonState.AddRange(
                new DbSeasonState(
                    seasonId,
                    AnalysisMode.Introduction,
                    AnalyzerAction.Default,
                    [excludedEpisodeId, includedItemId, stateOnlyExcludedId],
                    "intro-config",
                    [excludedMovieId, includedItemId]),
                new DbSeasonState(
                    seasonId,
                    AnalysisMode.Credits,
                    AnalyzerAction.Default,
                    [includedItemId],
                    "credits-config",
                    [stateOnlyExcludedId, includedItemId]));
            await db.SaveChangesAsync();
        }

        var libraryManager = EntrypointTestHelpers.CreateLibraryManager(
            CreateEpisode(excludedEpisodeId, seasonId, "The Office"),
            CreateMovie(excludedMovieId, "The Matrix"),
            CreateMovie(includedItemId, "Office Space"),
            CreateEpisode(stateOnlyExcludedId, seasonId, "The Office"));
        var refresher = new RecordingMediaSegmentRefresher();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(refresher, loggerFactory, libraryManager);

        var result = await controller.ClearExcludedTimestampsAsync(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var affectedIds = new[] { excludedEpisodeId, excludedMovieId, stateOnlyExcludedId };
        await using (var db = new IntroSkipperDbContext(dbPath))
        {
            Assert.False(await db.DbSegment.AnyAsync(s => affectedIds.Contains(s.ItemId)));
            Assert.True(await db.DbSegment.AnyAsync(s => s.ItemId == includedItemId));

            var seasonStates = await db.DbSeasonState.Where(s => s.SeasonId == seasonId).ToListAsync();
            Assert.All(seasonStates, state =>
            {
                Assert.DoesNotContain(excludedEpisodeId, state.EpisodeIds);
                Assert.DoesNotContain(excludedMovieId, state.EpisodeIds);
                Assert.DoesNotContain(stateOnlyExcludedId, state.EpisodeIds);
                Assert.DoesNotContain(excludedEpisodeId, state.SettledReanalysisEpisodeIds);
                Assert.DoesNotContain(excludedMovieId, state.SettledReanalysisEpisodeIds);
                Assert.DoesNotContain(stateOnlyExcludedId, state.SettledReanalysisEpisodeIds);
            });
            Assert.Contains(seasonStates, state => state.EpisodeIds.Contains(includedItemId));
            Assert.Contains(seasonStates, state => state.SettledReanalysisEpisodeIds.Contains(includedItemId));
        }

        Assert.Equal(1, refresher.CollectionCallCount);
        Assert.Equal(affectedIds.OrderBy(id => id), refresher.LastItemIds.OrderBy(id => id));
    }

    [Fact]
    public async Task ClearExcludedTimestampsAsync_NoConfiguredExclusions_DoesNotWriteOrRefresh()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "_dbPath", dbPath);
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration());
        EntrypointTestHelpers.SetPropertyOrField(plugin, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());

        await using (var db = new IntroSkipperDbContext(dbPath))
        {
            await db.Database.EnsureCreatedAsync();
            db.DbSegment.Add(new DbSegment(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Introduction));
            await db.SaveChangesAsync();
        }

        var refresher = new RecordingMediaSegmentRefresher();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(refresher, loggerFactory, EntrypointTestHelpers.CreateLibraryManager(CreateMovie(itemId, "The Matrix")));

        var result = await controller.ClearExcludedTimestampsAsync(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        await using (var db = new IntroSkipperDbContext(dbPath))
        {
            Assert.True(await db.DbSegment.AnyAsync(s => s.ItemId == itemId));
        }

        Assert.Equal(0, refresher.CollectionCallCount);
    }

    [Fact]
    public async Task ClearExcludedTimestampsAsync_RemovesPathExcludedStateAndRefreshesJellyfin()
    {
        var excludedEpisodeId = Guid.NewGuid();
        var excludedMovieId = Guid.NewGuid();
        var includedItemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "_dbPath", dbPath);
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration
        {
            ExcludePaths = "/mnt/remote",
            UpdateMediaSegments = false
        });
        EntrypointTestHelpers.SetPropertyOrField(plugin, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());

        await using (var db = new IntroSkipperDbContext(dbPath))
        {
            await db.Database.EnsureCreatedAsync();
            db.DbSegment.AddRange(
                new DbSegment(new Segment(excludedEpisodeId, new TimeRange(10, 20)), AnalysisMode.Introduction),
                new DbSegment(new Segment(excludedMovieId, new TimeRange(30, 40)), AnalysisMode.Credits),
                new DbSegment(new Segment(includedItemId, new TimeRange(50, 60)), AnalysisMode.Introduction));
            db.DbSeasonState.Add(new DbSeasonState(
                seasonId,
                AnalysisMode.Introduction,
                AnalyzerAction.Default,
                [excludedEpisodeId, includedItemId],
                "intro-config",
                [excludedMovieId, includedItemId]));
            await db.SaveChangesAsync();
        }

        var libraryManager = EntrypointTestHelpers.CreateLibraryManager(
            CreateEpisodeWithPath(excludedEpisodeId, seasonId, "Some Show", "/mnt/remote/Some Show/S01E01.mkv"),
            CreateMovieWithPath(excludedMovieId, "Some Movie", "/mnt/remote/Some Movie.mkv"),
            CreateMovieWithPath(includedItemId, "Local Movie", "/media/local/Local Movie.mkv"));
        var refresher = new RecordingMediaSegmentRefresher();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(refresher, loggerFactory, libraryManager);

        var result = await controller.ClearExcludedTimestampsAsync(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var affectedIds = new[] { excludedEpisodeId, excludedMovieId };
        await using (var db = new IntroSkipperDbContext(dbPath))
        {
            Assert.False(await db.DbSegment.AnyAsync(s => affectedIds.Contains(s.ItemId)));
            Assert.True(await db.DbSegment.AnyAsync(s => s.ItemId == includedItemId));

            var seasonStates = await db.DbSeasonState.Where(s => s.SeasonId == seasonId).ToListAsync();
            Assert.All(seasonStates, state =>
            {
                Assert.DoesNotContain(excludedEpisodeId, state.EpisodeIds);
                Assert.DoesNotContain(excludedMovieId, state.SettledReanalysisEpisodeIds);
            });
            Assert.Contains(seasonStates, state => state.EpisodeIds.Contains(includedItemId));
            Assert.Contains(seasonStates, state => state.SettledReanalysisEpisodeIds.Contains(includedItemId));
        }

        Assert.Equal(1, refresher.CollectionCallCount);
        Assert.Equal(affectedIds.OrderBy(id => id), refresher.LastItemIds.OrderBy(id => id));
    }

    private static VisualizationController CreateController(RecordingMediaSegmentRefresher refresher, ILoggerFactory loggerFactory, ILibraryManager? libraryManager = null)
    {
        return new VisualizationController(
            NullLogger<VisualizationController>.Instance,
            refresher,
            libraryManager: libraryManager ?? null!,
            providerManager: null!,
            fileSystem: null!,
            loggerFactory,
            ffmpegService: null!,
            new DetectionCacheService(NullLogger<DetectionCacheService>.Instance));
    }

    private static Episode CreateEpisode(Guid id, Guid seasonId, string seriesName)
    {
        var episode = EntrypointTestHelpers.CreateUninitialized<Episode>();
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", id);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeasonId", seasonId);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeriesName", seriesName);
        EntrypointTestHelpers.EnsureNonVirtual(episode);
        return episode;
    }

    private static Movie CreateMovie(Guid id, string name)
    {
        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", id);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Name", name);
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        return movie;
    }

    private static Episode CreateEpisodeWithPath(Guid id, Guid seasonId, string seriesName, string path)
    {
        var episode = CreateEpisode(id, seasonId, seriesName);
        EntrypointTestHelpers.SetPropertyOrField(episode, "Path", path);
        EntrypointTestHelpers.EnsureNonVirtual(episode);
        return episode;
    }

    private static Movie CreateMovieWithPath(Guid id, string name, string path)
    {
        var movie = CreateMovie(id, name);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Path", path);
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        return movie;
    }


    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope(string dbPath, Guid seriesId, Guid seasonId, IReadOnlyList<Guid> episodeIds, bool updateMediaSegments)
    {
        var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "_dbPath", dbPath);
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration { UpdateMediaSegments = updateMediaSegments });
        EntrypointTestHelpers.SetPropertyOrField(plugin, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());
        plugin.QueuedMediaItems[seasonId] =
        [
            new QueuedEpisode { SeriesId = seriesId, SeasonId = seasonId, EpisodeId = episodeIds[0], Name = "Episode 1" },
            new QueuedEpisode { SeriesId = seriesId, SeasonId = seasonId, EpisodeId = episodeIds[1], Name = "Episode 2" }
        ];
        return scope;
    }

    private static async Task SeedSeasonAsync(string dbPath, Guid seasonId, IReadOnlyList<Guid> episodeIds)
    {
        await using var db = new IntroSkipperDbContext(dbPath);
        await db.Database.EnsureCreatedAsync();
        db.DbSegment.AddRange(
            new DbSegment(new Segment(episodeIds[0], new TimeRange(10, 20)), AnalysisMode.Introduction),
            new DbSegment(new Segment(episodeIds[1], new TimeRange(30, 40)), AnalysisMode.Introduction));
        db.DbSeasonState.AddRange(
            new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default, episodeIds),
            new DbSeasonState(seasonId, AnalysisMode.Credits, AnalyzerAction.Default, episodeIds));
        await db.SaveChangesAsync();
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
    }
}
