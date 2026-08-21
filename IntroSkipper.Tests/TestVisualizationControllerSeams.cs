using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestVisualizationControllerSeams
{
    [Fact]
    public async Task GetAnalyzerAction_ReturnsNotFound_WhenSeasonIsNotQueued()
    {
        using var scope = CreatePluginScope();
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);

        var result = await controller.GetAnalyzerAction(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetAnalyzerAction_ReturnsStoredAndDefaultActions_WhenSeasonIsQueued()
    {
        using var scope = CreatePluginScope();
        var seasonId = Guid.NewGuid();
        QueueSeason(seasonId, Guid.NewGuid());
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.SetAnalyzerActionAsync(
            seasonId,
            new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Chapter });
        var controller = CreateController(database, scope.CacheDbPath);

        var result = await controller.GetAnalyzerAction(seasonId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var actions = Assert.IsAssignableFrom<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>>(ok.Value);
        Assert.Equal(AnalyzerAction.Chapter, actions[AnalysisMode.Introduction]);
        Assert.Equal(AnalyzerAction.Default, actions[AnalysisMode.Credits]);
    }

    [Fact]
    public void GetSeasonEpisodes_ReturnsQueuedEpisodes_WhenSeriesMatches()
    {
        using var scope = CreatePluginScope();
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var firstEpisodeId = Guid.NewGuid();
        var secondEpisodeId = Guid.NewGuid();
        QueueSeason(seasonId, firstEpisodeId, seriesId, "First", secondEpisodeId, seriesId, "Second");
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);

        var result = controller.GetSeasonEpisodes(seriesId, seasonId);

        Assert.Equal(
            [new EpisodeVisualization(firstEpisodeId, "First"), new EpisodeVisualization(secondEpisodeId, "Second")],
            result.Value);
    }

    [Fact]
    public void GetSeasonEpisodes_ReturnsNotFound_WhenSeasonIsMissingOrSeriesDoesNotMatch()
    {
        using var scope = CreatePluginScope();
        var seasonId = Guid.NewGuid();
        QueueSeason(seasonId, Guid.NewGuid());
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);

        var missingSeason = controller.GetSeasonEpisodes(Guid.NewGuid(), Guid.NewGuid());
        var wrongSeries = controller.GetSeasonEpisodes(Guid.NewGuid(), seasonId);

        Assert.IsType<NotFoundResult>(missingSeason.Result);
        Assert.IsType<NotFoundResult>(wrongSeries.Result);
    }

    [Fact]
    public async Task UpdateAnalyzerActions_PersistsRequestAndReturnsNoContent()
    {
        using var scope = CreatePluginScope();
        var seasonId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var controller = CreateController(database, scope.CacheDbPath);
        var request = new UpdateAnalyzerActionsRequest
        {
            Id = seasonId,
            AnalyzerActions = new Dictionary<AnalysisMode, AnalyzerAction>
            {
                [AnalysisMode.Introduction] = AnalyzerAction.BlackFrame,
            },
        };

        var result = await controller.UpdateAnalyzerActions(request);
        var actions = await database.GetAllAnalyzerActionsAsync(seasonId);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(AnalyzerAction.BlackFrame, actions[AnalysisMode.Introduction]);
        Assert.Equal(AnalyzerAction.Default, actions[AnalysisMode.Credits]);
    }

    [Fact]
    public async Task GetScanStatus_ReflectsHeldScanLease()
    {
        using var scope = CreatePluginScope();
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);
        Assert.False(Assert.IsType<ScanStatusResponse>(controller.GetScanStatus().Value).IsRunning);

        var lease = Assert.IsAssignableFrom<IDisposable>(await ScheduledTaskSemaphore.TryAcquireAsync());
        try
        {
            Assert.True(Assert.IsType<ScanStatusResponse>(controller.GetScanStatus().Value).IsRunning);
        }
        finally
        {
            lease.Dispose();
        }

        Assert.False(Assert.IsType<ScanStatusResponse>(controller.GetScanStatus().Value).IsRunning);
    }

    [Fact]
    public async Task ScanSeason_ReturnsConflict_WhenScanLeaseIsHeld()
    {
        using var scope = CreatePluginScope();
        var controller = CreateController(
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            scope.CacheDbPath,
            EntrypointTestHelpers.CreateLibraryManager());
        var lease = Assert.IsAssignableFrom<IDisposable>(await ScheduledTaskSemaphore.TryAcquireAsync());
        try
        {
            var result = await controller.ScanSeason(Guid.NewGuid(), Guid.NewGuid());

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal("A scan is already in progress.", conflict.Value!.GetType().GetProperty("message")!.GetValue(conflict.Value));
            Assert.True(ScheduledTaskSemaphore.IsBusy);
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public async Task ScanSeason_ReturnsAccepted_AndReleasesItsBackgroundLease()
    {
        using var scope = CreatePluginScope();
        var controller = CreateController(
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            scope.CacheDbPath,
            EntrypointTestHelpers.CreateLibraryManager());

        var result = await controller.ScanSeason(Guid.NewGuid(), Guid.NewGuid(), new CancellationToken(canceled: true));

        Assert.IsType<AcceptedResult>(result);
        await WaitForScanLeaseReleaseAsync();
    }

    private static VisualizationController CreateController(IIntroSkipperDatabase database, string cacheDbPath, ILibraryManager? libraryManager = null)
        => new(
            NullLogger<VisualizationController>.Instance,
            new NoOpMediaSegmentRefresher(),
            new RecordingSegmentChange(),
            libraryManager!,
            new AnalyzerTaskFactory(
                NullLoggerFactory.Instance,
                libraryManager!,
                providerManager: null!,
                fileSystem: null!,
                new NoOpMediaSegmentRefresher(),
                ffmpegService: null!,
                DatabaseTestHelpers.CreateCacheService(cacheDbPath),
                database),
            database,
            DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath));

    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope()
    {
        var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());
        return scope;
    }

    private static void QueueSeason(Guid seasonId, Guid episodeId)
        => QueueSeason(seasonId, episodeId, Guid.NewGuid(), "Episode");

    private static void QueueSeason(Guid seasonId, Guid firstEpisodeId, Guid firstSeriesId, string firstName, Guid? secondEpisodeId = null, Guid? secondSeriesId = null, string? secondName = null)
    {
        Plugin.Instance!.QueuedMediaItems[seasonId] =
        [
            new QueuedEpisode { EpisodeId = firstEpisodeId, SeasonId = seasonId, SeriesId = firstSeriesId, Name = firstName },
        ];

        if (secondEpisodeId is not null)
        {
            Plugin.Instance.QueuedMediaItems[seasonId].Add(
                new QueuedEpisode { EpisodeId = secondEpisodeId.Value, SeasonId = seasonId, SeriesId = secondSeriesId!.Value, Name = secondName! });
        }
    }

    private static async Task WaitForScanLeaseReleaseAsync()
    {
        for (var attempt = 0; ScheduledTaskSemaphore.IsBusy && attempt < 100; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.False(ScheduledTaskSemaphore.IsBusy);
    }

    private sealed class NoOpMediaSegmentRefresher : IMediaSegmentRefresher
    {
        public Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
