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
        var seasonInfos = await db.DbSeasonInfo.Where(s => s.SeasonId == seasonId).ToListAsync();
        Assert.All(seasonInfos, info => Assert.Empty(info.EpisodeIds));
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

    private static VisualizationController CreateController(RecordingMediaSegmentRefresher refresher, ILoggerFactory loggerFactory)
    {
        return new VisualizationController(
            NullLogger<VisualizationController>.Instance,
            refresher,
            libraryManager: null!,
            providerManager: null!,
            fileSystem: null!,
            loggerFactory,
            ffmpegService: null!,
            new DetectionCacheService(NullLogger<DetectionCacheService>.Instance));
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
        db.DbSeasonInfo.AddRange(
            new DbSeasonInfo(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default, episodeIds),
            new DbSeasonInfo(seasonId, AnalysisMode.Credits, AnalyzerAction.Default, episodeIds));
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
            LastItemIds = itemIds.ToArray();
            return Completion?.Task ?? Task.CompletedTask;
        }
    }
}
