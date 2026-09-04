using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestVisualizationController
{
    [Fact]
    public async Task EraseSeasonAsync_AwaitsMirrorConvergence_BeforeReturningNoContent()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = DatabaseTestHelpers.CreateTempDbPath($"{Guid.NewGuid():N}-visualization.db");
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);
        await SeedSeasonAsync(dbPath, seasonId, episodeIds);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Both episodes' rows are mirrored, so the post-erase convergence has real
        // writes; the first one parks so the pre-completion assertion cannot race.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(episodeIds[0]), CreateMirroredDto(episodeIds[1])],
            WriteGate = writeGate,
            WriteEntered = writeEntered,
            BlockedItemId = episodeIds[0]
        };
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        await database.InitializeAsync();
        var controller = CreateController(database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        var actionTask = controller.EraseSeasonAsync(seriesId, seasonId, eraseCache: false, CancellationToken.None);
        await writeEntered.Task;

        // The erase is committed and journaled; the response still waits for the
        // convergence pass it kicked off.
        Assert.False(actionTask.IsCompleted);

        writeGate.SetResult();
        var result = await actionTask;

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await store.GetOwnSegmentsAsync(episodeIds[0], CancellationToken.None));
        Assert.Empty(await store.GetOwnSegmentsAsync(episodeIds[1], CancellationToken.None));
        await using var db = DatabaseTestHelpers.CreateSegmentContext(dbPath);

        // Covers the seeded tombstone as well: a season erase deletes suppressed rows too.
        Assert.False(await db.Segments.AnyAsync(s => episodeIds.Contains(s.ItemId)));
        Assert.False(await db.AnalyzedItems.AnyAsync(a => episodeIds.Contains(a.ItemId)));
    }

    [Fact]
    public async Task EraseSeasonAsync_CacheFailureStillClearsMainDatabaseState()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = DatabaseTestHelpers.CreateTempDbPath($"{Guid.NewGuid():N}-visualization.db");
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: false);
        await SeedSeasonAsync(dbPath, seasonId, episodeIds);
        var controller = CreateController(DatabaseTestHelpers.CreateSegmentDatabase(dbPath), MissingCachePath());

        var result = await controller.EraseSeasonAsync(
            seriesId,
            seasonId,
            eraseCache: true,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        await using var db = DatabaseTestHelpers.CreateSegmentContext(dbPath);

        // Covers the seeded tombstone as well: a season erase deletes suppressed rows too.
        Assert.False(await db.Segments.AnyAsync(s => episodeIds.Contains(s.ItemId)));
        Assert.False(await db.AnalyzedItems.AnyAsync(a => episodeIds.Contains(a.ItemId)));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ClearExcludedTimestampsAsync_RemovesOnlyCurrentlyExcludedState(bool cacheAvailable, int expectedRemovedCacheEntries)
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var excludedId = Guid.NewGuid();
        var includedId = Guid.NewGuid();
        var dbPath = DatabaseTestHelpers.CreateTempDbPath($"{Guid.NewGuid():N}-visualization.db");
        var config = new PluginConfiguration
        {
            UpdateMediaSegments = true,
            SeriesExclusions = { "Excluded Show" }
        };
        using var pluginScope = CreatePluginScope(seriesId, seasonId, [excludedId, includedId], config);
        await SeedSeasonAsync(dbPath, seasonId, [excludedId, includedId]);
        SeedCache(pluginScope.CacheDbPath, excludedId, includedId);

        // The controller enumerates the library through a fresh queue manager, so the
        // exclusion policy decides from the enumerated series names.
        var libraryManager = EntrypointTestHelpers.FakeLibraryManager.Create(
            [new VirtualFolderInfo { Name = "Shows", ItemId = Guid.NewGuid().ToString() }],
            _ => [CreateLibraryEpisode(excludedId, seriesId, seasonId, "Excluded Show"), CreateLibraryEpisode(includedId, seriesId, seasonId, "Included Show")]);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", libraryManager);
        var controller = CreateController(
            DatabaseTestHelpers.CreateSegmentDatabase(dbPath),
            cacheAvailable ? pluginScope.CacheDbPath : MissingCachePath(),
            libraryManager: libraryManager);

        var result = await controller.ClearExcludedTimestampsAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ClearExcludedTimestampsResponse>(ok.Value);
        Assert.Equal(1, response.AffectedItems);

        // The excluded episode's active segment plus its tombstone: clearing excluded
        // items is a full erase, so suppressed rows are deleted (and counted) too. A
        // failing cache database does not fail the request.
        Assert.Equal(2, response.RemovedSegments);
        Assert.Equal(expectedRemovedCacheEntries, response.RemovedCacheEntries);

        await using var db = DatabaseTestHelpers.CreateSegmentContext(dbPath);
        Assert.False(await db.Segments.AnyAsync(s => s.ItemId == excludedId));
        Assert.True(await db.Segments.AnyAsync(s => s.ItemId == includedId));
        Assert.False(await db.AnalyzedItems.AnyAsync(a => a.ItemId == excludedId));
        Assert.True(await db.AnalyzedItems.AnyAsync(a => a.ItemId == includedId));

        using var cacheDb = DatabaseTestHelpers.CreateCacheContext(pluginScope.CacheDbPath);
        Assert.Equal(!cacheAvailable, await cacheDb.DetectionCache.AnyAsync(e => e.ItemId == excludedId));
        Assert.True(await cacheDb.DetectionCache.AnyAsync(e => e.ItemId == includedId));
    }

    [Fact]
    public async Task DisabledItems_PutGetDelete_RoundTripsAndRefreshes()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(CreateEpisodeItem(episodeIds[0], seasonId)));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // Steady state before the toggle: an automatic segment already mirrored to
        // Jellyfin. Disabling pushes the empty replace that withdraws it; enabling
        // pushes it back, each direction a real write, not a skipped no-op sync.
        await database.ReplaceAutoSegmentsAsync(
            episodeIds[0], AnalysisMode.Introduction, [new Segment(episodeIds[0], new TimeRange(10, 20))], SegmentSource.Chapter);
        var mirroredRow = Assert.Single(await database.GetSegmentsAsync(episodeIds[0]));
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(episodeIds[0], mirroredRow.Id, mirroredRow.StartTicks, mirroredRow.EndTicks)]
        };
        var controller = CreateController(database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        var putResult = await controller.DisableItem(episodeIds[0], CancellationToken.None);

        Assert.IsType<NoContentResult>(putResult);
        Assert.Equal(episodeIds[0], Assert.Single(store.ReplacedItems).ItemId);
        Assert.Equal([episodeIds[0]], await database.GetDisabledItemIdsAsync(seasonId));

        var getResult = await controller.GetDisabledItems(seasonId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(getResult.Result);
        var ids = Assert.IsAssignableFrom<IReadOnlySet<Guid>>(ok.Value);
        Assert.Equal([episodeIds[0]], ids);

        var deleteResult = await controller.EnableItem(episodeIds[0], CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResult);

        // Both directions resync the item's mirror through the change coordinator.
        Assert.Equal(2, store.WriteCallCount);
        Assert.Empty(await database.GetDisabledItemIdsAsync(seasonId));
    }

    [Fact]
    public async Task DisabledItems_EpisodeWithoutSeason_FallsBackToItsOwnKey()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var orphanId = Guid.NewGuid();
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);

        // The episode is not in the cached queue and Jellyfin resolved no season for
        // it, so SeasonStateKeyResolver's last resort reports Guid.Empty.
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(CreateEpisodeItem(orphanId, Guid.Empty)));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var controller = CreateController(database, pluginScope.CacheDbPath);

        var result = await controller.DisableItem(orphanId, CancellationToken.None);

        // The toggle records the item's own id as its key (the movie convention)
        // instead of rejecting the intent over the empty season key.
        Assert.IsType<NoContentResult>(result);
        Assert.Equal([orphanId], await database.GetDisabledItemIdsAsync(orphanId));
    }

    [Fact]
    public async Task DisabledItems_RejectUnknownItem()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(CreateEpisodeItem(episodeIds[0], seasonId)));
        var store = new FakeJellyfinSegmentStore();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var controller = CreateController(database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        var unknown = await controller.DisableItem(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(unknown);
        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(await database.GetDisabledItemIdsAsync(seasonId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledItems_RefreshFailureKeepsFlag_AndReportsAcceptedPending(bool disable)
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(CreateEpisodeItem(episodeIds[0], seasonId)));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // A stored automatic segment gives the sync rows to push (enable) or withdraw
        // (disable, where the row is also mirrored), so it is a real (failing) write
        // instead of a skipped no-op.
        await database.ReplaceAutoSegmentsAsync(
            episodeIds[0], AnalysisMode.Introduction, [new Segment(episodeIds[0], new TimeRange(10, 20))], SegmentSource.Chapter);
        if (!disable)
        {
            await database.SetItemDisabledAsync(seasonId, episodeIds[0], disabled: true);
        }

        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = disable ? [CreateMirroredDto(episodeIds[0])] : [],
            WriteException = new InvalidOperationException("mirror write failed")
        };
        var controller = CreateController(database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        var result = disable
            ? await controller.DisableItem(episodeIds[0], CancellationToken.None)
            : await controller.EnableItem(episodeIds[0], CancellationToken.None);

        // The flag committed durably with its projection work; the failed mirror
        // write surfaces as accepted-plus-pending, never a rollback that would make
        // the stored flag disagree with recorded intent.
        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal("Pending", Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value).Projection);
        Assert.Equal(disable ? [episodeIds[0]] : [], await database.GetDisabledItemIdsAsync(seasonId));
    }

    [Fact]
    public async Task DisabledItems_ConcurrentSegmentCreateSerializesBehindFailingProjection()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: true);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(CreateEpisodeItem(episodeIds[0], seasonId)));
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episodeIds[0], AnalysisMode.Introduction, [new Segment(episodeIds[0], new TimeRange(10, 20))], SegmentSource.Chapter);

        // Steady state: the automatic segment is already mirrored, so A's disable-sync
        // has a row to withdraw and parks in a real write instead of skipping.
        var introRow = Assert.Single(await database.GetSegmentsAsync(episodeIds[0]));
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(episodeIds[0], introRow.Id, introRow.StartTicks, introRow.EndTicks)],
            WriteGate = writeGate,
            WriteEntered = writeEntered,
            BlockedItemId = episodeIds[0]
        };
        var segmentChange = DatabaseTestHelpers.CreateSegmentChange(store, database);
        var controller = CreateController(database, pluginScope.CacheDbPath, segmentChange);
        var segmentsController = new SegmentsController(database, segmentChange);

        // Request A commits the disable flag, then parks inside its projection write
        // while holding the item's mutation stripe.
        var requestA = controller.DisableItem(episodeIds[0], CancellationToken.None);
        await writeEntered.Task;

        // A segment create for the same item must serialize behind the stripe: a
        // create interleaving with A's projection would race the mirror push.
        var requestB = segmentsController.CreateSegment(
            episodeIds[0], new CreateSegmentRequest(AnalysisMode.Commercial, 100, 120), CancellationToken.None);
        await Task.Yield();
        Assert.False(requestB.IsCompleted);
        Assert.Equal(1, store.WriteCallCount);

        // A's mirror write now fails: the committed flag stands (no rollback), A
        // reports accepted-plus-pending, and B proceeds against the disabled item.
        writeGate.SetException(new InvalidOperationException("mirror write failed"));

        var acceptedA = Assert.IsType<AcceptedResult>(await requestA);
        Assert.Equal("Pending", Assert.IsType<SegmentChangeAcceptedResponse>(acceptedA.Value).Projection);
        Assert.IsType<CreatedAtActionResult>((await requestB).Result);

        // B's projection ran with the disable committed: the final push withholds the
        // automatic segment and carries only the new user segment (which keeps
        // syncing on disabled items), converging the mirror A's failure left behind.
        Assert.Equal([episodeIds[0]], await database.GetDisabledItemIdsAsync(seasonId));
        Assert.Equal(2, store.WriteCallCount);
        var finalPush = store.ReplacedItems[^1];
        Assert.Equal(episodeIds[0], finalPush.ItemId);
        var pushedSegment = Assert.Single(finalPush.Segments);
        Assert.Equal(MediaSegmentType.Commercial, pushedSegment.Type);
    }

    [Fact]
    public async Task DisabledItems_MovieUsesItsOwnIdAsSeasonKey()
    {
        var movieId = Guid.NewGuid();
        using var pluginScope = CreatePluginScope(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], updateMediaSegments: true);
        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", movieId);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(movie));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var controller = CreateController(database, pluginScope.CacheDbPath);

        var result = await controller.DisableItem(movieId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        // The server records the movie's own ID as its season key, which is what
        // the dashboard's movie view lists by.
        Assert.Equal([movieId], await database.GetDisabledItemIdsAsync(movieId));
    }

    [Fact]
    public async Task AnalyzerActions_RoundTripThroughTheEndpoints_And404ForUnqueuedSeasons()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seasonId = Guid.NewGuid();
        QueueSeason(seasonId, Guid.NewGuid(), (Guid.NewGuid(), "Episode"));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var controller = CreateController(database, scope.CacheDbPath);

        Assert.IsType<NotFoundResult>((await controller.GetAnalyzerAction(Guid.NewGuid())).Result);

        var update = await controller.UpdateAnalyzerActions(new UpdateAnalyzerActionsRequest
        {
            Id = seasonId,
            AnalyzerActions = new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Chapter },
        });

        Assert.IsType<NoContentResult>(update);
        var ok = Assert.IsType<OkObjectResult>((await controller.GetAnalyzerAction(seasonId)).Result);
        var actions = Assert.IsAssignableFrom<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>>(ok.Value);
        Assert.Equal(AnalyzerAction.Chapter, actions[AnalysisMode.Introduction]);
        Assert.Equal(AnalyzerAction.Default, actions[AnalysisMode.Credits]);
    }

    [Fact]
    public void GetSeasonEpisodes_ReturnsQueuedEpisodes_WhenSeriesMatches()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seasonId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var firstEpisodeId = Guid.NewGuid();
        var secondEpisodeId = Guid.NewGuid();
        QueueSeason(seasonId, seriesId, (firstEpisodeId, "First"), (secondEpisodeId, "Second"));
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);

        var result = controller.GetSeasonEpisodes(seriesId, seasonId);

        Assert.Equal(
            [new EpisodeVisualization(firstEpisodeId, "First"), new EpisodeVisualization(secondEpisodeId, "Second")],
            result.Value);
    }

    [Fact]
    public void GetSeasonEpisodes_ReturnsNotFound_WhenSeasonIsMissingOrSeriesDoesNotMatch()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var seasonId = Guid.NewGuid();
        QueueSeason(seasonId, Guid.NewGuid(), (Guid.NewGuid(), "Episode"));
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);

        var missingSeason = controller.GetSeasonEpisodes(Guid.NewGuid(), Guid.NewGuid());
        var wrongSeries = controller.GetSeasonEpisodes(Guid.NewGuid(), seasonId);

        Assert.IsType<NotFoundResult>(missingSeason.Result);
        Assert.IsType<NotFoundResult>(wrongSeries.Result);
    }

    [Fact]
    public void GetScanStatus_ReflectsHeldScanLease()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);
        Assert.False(Assert.IsType<ScanStatusResponse>(controller.GetScanStatus().Value).IsRunning);

        var lease = Assert.IsAssignableFrom<IDisposable>(ScheduledTaskSemaphore.TryAcquire());
        try
        {
            Assert.True(Assert.IsType<ScanStatusResponse>(controller.GetScanStatus().Value).IsRunning);
            Assert.Null(ScheduledTaskSemaphore.TryAcquire());
        }
        finally
        {
            lease.Dispose();
        }

        Assert.False(Assert.IsType<ScanStatusResponse>(controller.GetScanStatus().Value).IsRunning);
    }

    // The semaphore half of ScanState is covered through the endpoint above; this pins the
    // worker half, which keeps the endpoint and the support bundle in agreement while the
    // detection task worker is active outside its semaphore lease.
    [Fact]
    public void ScanState_ReportsRunning_WhileDetectTaskWorkerIsActive()
    {
        Assert.False(ScanState.IsRunning(null));
        Assert.False(ScanState.IsRunning(ScheduledTaskWorkerStub.Create(TaskState.Idle)));
        Assert.True(ScanState.IsRunning(ScheduledTaskWorkerStub.Create(TaskState.Running)));
        Assert.True(ScanState.IsRunning(ScheduledTaskWorkerStub.Create(TaskState.Cancelling)));
    }

    [Fact]
    public void ScanSeason_ReturnsConflict_WhenScanLeaseIsHeld()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);
        var lease = Assert.IsAssignableFrom<IDisposable>(ScheduledTaskSemaphore.TryAcquire());
        try
        {
            var result = controller.ScanSeason(Guid.NewGuid(), Guid.NewGuid());

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
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var controller = CreateController(DatabaseTestHelpers.CreateTempSegmentDatabase(), scope.CacheDbPath);

        var result = controller.ScanSeason(Guid.NewGuid(), Guid.NewGuid(), new CancellationToken(canceled: true));

        Assert.IsType<AcceptedResult>(result);
        for (var attempt = 0; ScheduledTaskSemaphore.IsBusy && attempt < 100; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.False(ScheduledTaskSemaphore.IsBusy);
    }

    private static Episode CreateEpisodeItem(Guid episodeId, Guid seasonId)
    {
        var episode = new Episode();
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeasonId", seasonId);
        return episode;
    }

    // An episode the queue manager can enumerate: it needs a path, a season and a series name.
    private static Episode CreateLibraryEpisode(Guid episodeId, Guid seriesId, Guid seasonId, string seriesName)
    {
        var episode = new Episode
        {
            Name = "Episode",
            SeriesId = seriesId,
            SeasonId = seasonId,
            ParentIndexNumber = 1,
            IndexNumber = 1,
            Path = $"/media/{seriesName}/s01e01.mkv",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeriesName", seriesName);
        EntrypointTestHelpers.EnsureNonVirtual(episode);
        return episode;
    }

    /// <summary>
    /// A row already mirrored to Jellyfin. Seeded so a sync whose intended push differs
    /// (a disable withdrawing it, or a plugin-side change) is a real write rather than
    /// a skipped no-op; defaults produce a row matching no plugin row.
    /// </summary>
    private static MediaSegmentDto CreateMirroredDto(Guid itemId, Guid? id = null, long? startTicks = null, long? endTicks = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            ItemId = itemId,
            Type = MediaSegmentType.Intro,
            StartTicks = startTicks ?? TickConversions.FromSeconds(10),
            EndTicks = endTicks ?? TickConversions.FromSeconds(20)
        };

    // A cache database path whose directory does not exist, so every cache operation fails.
    private static string MissingCachePath()
        => Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "visualization-controller", Guid.NewGuid().ToString("N"), "cache.db");

    private static VisualizationController CreateController(
        IntroSkipperDatabase database,
        string cacheDbPath,
        SegmentChange? segmentChange = null,
        ILibraryManager? libraryManager = null)
        => new(
            NullLogger<VisualizationController>.Instance,
            segmentChange ?? DatabaseTestHelpers.CreateSegmentChange(new FakeJellyfinSegmentStore(), database),
            new AnalyzerTaskFactory(
                NullLoggerFactory.Instance,
                libraryManager!,
                providerManager: null!,
                fileSystem: null!,
                ffmpegService: null!,
                DatabaseTestHelpers.CreateCacheService(cacheDbPath),
                database),
            database,
            DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath),
            EntrypointTestHelpers.CreateTaskManager());

    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope(Guid seriesId, Guid seasonId, IReadOnlyList<Guid> episodeIds, bool updateMediaSegments)
        => CreatePluginScope(
            seriesId,
            seasonId,
            episodeIds,
            new PluginConfiguration { UpdateMediaSegments = updateMediaSegments });

    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope(Guid seriesId, Guid seasonId, IReadOnlyList<Guid> episodeIds, PluginConfiguration config)
    {
        var scope = EntrypointTestHelpers.CreatePluginScope(config);
        QueueSeason(seasonId, seriesId, (episodeIds[0], "Episode 1"), (episodeIds[1], "Episode 2"));
        return scope;
    }

    private static void QueueSeason(Guid seasonId, Guid seriesId, params (Guid EpisodeId, string Name)[] episodes)
        => Plugin.Instance!.QueuedMediaItems[seasonId] =
            [.. episodes.Select(e => new QueuedEpisode { EpisodeId = e.EpisodeId, SeasonId = seasonId, SeriesId = seriesId, Name = e.Name })];

    private static async Task SeedSeasonAsync(string dbPath, Guid seasonId, IReadOnlyList<Guid> episodeIds)
    {
        await using var db = DatabaseTestHelpers.CreateSegmentContext(dbPath);
        await db.Database.MigrateAsync();
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
            new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default),
            new DbSeasonState(seasonId, AnalysisMode.Credits, AnalyzerAction.Default));
        db.AnalyzedItems.AddRange(episodeIds.SelectMany(id => new[]
        {
            new DbAnalyzedItem(id, AnalysisMode.Introduction, "hash"),
            new DbAnalyzedItem(id, AnalysisMode.Credits, "hash")
        }));
        await db.SaveChangesAsync();
    }

    private static void SeedCache(string cacheDbPath, params Guid[] itemIds)
    {
        var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
        foreach (var itemId in itemIds)
        {
            cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0, EntrypointTestHelpers.EmptyJsonArray, string.Empty);
        }
    }

    private class ScheduledTaskWorkerStub : System.Reflection.DispatchProxy
    {
        private TaskState _state;

        public static IScheduledTaskWorker Create(TaskState state)
        {
            var proxy = Create<IScheduledTaskWorker, ScheduledTaskWorkerStub>();
            ((ScheduledTaskWorkerStub)(object)proxy)._state = state;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == $"get_{nameof(IScheduledTaskWorker.State)}")
            {
                return _state;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }
}
