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
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        var dbPath = CreateTempDbPath();
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
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        await database.InitializeAsync();
        var controller = CreateController(loggerFactory, database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

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
        await using var db = new IntroSkipperDbContext(dbPath);

        // Covers the seeded tombstone as well: a season erase deletes suppressed rows too.
        Assert.False(await db.Segments.AnyAsync(s => episodeIds.Contains(s.ItemId)));
        Assert.False(await db.AnalyzedItems.AnyAsync(a => episodeIds.Contains(a.ItemId)));
    }

    [Fact]
    public async Task EraseSeasonAsync_MirroringDisabled_CommitsEraseAndKeepsWorkJournaled()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(seriesId, seasonId, episodeIds, updateMediaSegments: false);
        await SeedSeasonAsync(dbPath, seasonId, episodeIds);
        var store = new FakeJellyfinSegmentStore();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(loggerFactory, database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        var result = await controller.EraseSeasonAsync(seriesId, seasonId, eraseCache: false, CancellationToken.None);

        // The erase commits regardless of the mirror flag; the journaled projection
        // work sits durably (state Skipped) and replays when mirroring turns on.
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, store.WriteCallCount);
        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.False(await db.Segments.AnyAsync(s => episodeIds.Contains(s.ItemId)));
        Assert.Equal(2, await db.ProjectionQueue.CountAsync(q => episodeIds.Contains(q.ItemId)));
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
        Assert.False(await db.AnalyzedItems.AnyAsync(a => episodeIds.Contains(a.ItemId)));
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
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var controller = CreateController(loggerFactory, dbPath, pluginScope.CacheDbPath);

        var result = await controller.ClearExcludedTimestampsAsync(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ClearExcludedTimestampsResponse>(ok.Value);
        Assert.Equal(1, response.AffectedItems);

        // The excluded episode's active segment plus its tombstone: clearing excluded
        // items is a full erase, so suppressed rows are deleted (and counted) too.
        Assert.Equal(2, response.RemovedSegments);
        Assert.Equal(1, response.RemovedCacheEntries);

        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.False(await db.Segments.AnyAsync(s => s.ItemId == excludedId));
        Assert.True(await db.Segments.AnyAsync(s => s.ItemId == includedId));
        Assert.False(await db.AnalyzedItems.AnyAsync(a => a.ItemId == excludedId));
        Assert.True(await db.AnalyzedItems.AnyAsync(a => a.ItemId == includedId));

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
        Assert.False(await db.AnalyzedItems.AnyAsync(a => a.ItemId == excludedId));
        Assert.True(await db.AnalyzedItems.AnyAsync(a => a.ItemId == includedId));
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
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

        // Steady state before the toggle: an automatic segment already mirrored to
        // Jellyfin. Disabling pushes the empty replace that withdraws it; enabling
        // pushes it back — each direction a real write, not a skipped no-op sync.
        await database.ReplaceAutoSegmentsAsync(
            episodeIds[0], AnalysisMode.Introduction, [new Segment(episodeIds[0], new TimeRange(10, 20))], SegmentSource.Chapter);
        var mirroredRow = Assert.Single(await database.GetSegmentsAsync(episodeIds[0]));
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(episodeIds[0], mirroredRow.Id, mirroredRow.StartTicks, mirroredRow.EndTicks)]
        };
        var controller = CreateController(
            loggerFactory, database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

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
    public async Task DisabledItems_RejectUnknownItem()
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
        var store = new FakeJellyfinSegmentStore();
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(
            loggerFactory, database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        var unknown = await controller.DisableItem(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(unknown);
        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(await database.GetDisabledItemIdsAsync(seasonId));
    }

    [Fact]
    public async Task DisabledItems_RefreshFailureKeepsDisable_AndReportsAcceptedPending()
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
        // A mirrored row makes the disable-sync a real (failing) write instead of a
        // skipped no-op.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(episodeIds[0])],
            WriteException = new InvalidOperationException("mirror write failed")
        };
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(
            loggerFactory, database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        var result = await controller.DisableItem(episodeIds[0], CancellationToken.None);

        // The flag committed durably with its projection work; the failed mirror
        // write surfaces as accepted-plus-pending — never a rollback that would make
        // the stored flag disagree with recorded intent.
        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal("Pending", Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value).Projection);
        Assert.Equal([episodeIds[0]], await database.GetDisabledItemIdsAsync(seasonId));
    }

    [Fact]
    public async Task DisabledItems_RefreshFailureKeepsEnable_AndReportsAcceptedPending()
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
        var store = new FakeJellyfinSegmentStore
        {
            WriteException = new InvalidOperationException("mirror write failed")
        };
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        await database.SetItemDisabledAsync(seasonId, episodeIds[0], disabled: true);

        // A stored automatic segment gives the enable-sync rows to push, so it is a
        // real (failing) write instead of a skipped no-op.
        await database.ReplaceAutoSegmentsAsync(
            episodeIds[0], AnalysisMode.Introduction, [new Segment(episodeIds[0], new TimeRange(10, 20))], SegmentSource.Chapter);
        var controller = CreateController(
            loggerFactory, database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        var result = await controller.EnableItem(episodeIds[0], CancellationToken.None);

        // The enable committed; Jellyfin temporarily lags behind and the journaled
        // work converges it — the stored flag records the user's intent, not the
        // mirror's transient failure.
        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal("Pending", Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value).Projection);
        Assert.Empty(await database.GetDisabledItemIdsAsync(seasonId));
    }

    [Fact]
    public async Task DisabledItems_ConcurrentMutationSerializesBehindFailingProjection()
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
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A mirrored row gives each disable-sync something to withdraw, so both are
        // real writes instead of skipped no-op syncs.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(episodeIds[0])],
            WriteGate = writeGate,
            WriteEntered = writeEntered,
            BlockedItemId = episodeIds[0]
        };
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        await database.InitializeAsync();
        var controller = CreateController(
            loggerFactory, database, pluginScope.CacheDbPath, DatabaseTestHelpers.CreateSegmentChange(store, database));

        // Request A commits the flag, then parks inside its projection write while
        // holding the item's mutation stripe.
        var requestA = controller.DisableItem(episodeIds[0], CancellationToken.None);
        await writeEntered.Task;

        // Request B mutates the same item while A's projection is in flight. It must
        // serialize behind the stripe: a projection interleaving with another
        // mutation's write would push state derived from a stale read.
        var requestB = controller.DisableItem(episodeIds[0], CancellationToken.None);
        Assert.NotSame(requestB, await Task.WhenAny(requestB, Task.Delay(250)));
        Assert.Equal(1, store.WriteCallCount);

        // A's mirror write now fails: A reports accepted-plus-pending (the flag
        // stays committed), and B's idempotent re-toggle journals a re-projection
        // that heals the diverged mirror.
        writeGate.SetException(new InvalidOperationException("mirror write failed"));

        var acceptedA = Assert.IsType<AcceptedResult>(await requestA);
        Assert.Equal("Pending", Assert.IsType<SegmentChangeAcceptedResponse>(acceptedA.Value).Projection);
        Assert.IsType<NoContentResult>(await requestB);

        Assert.Equal(2, store.WriteCallCount);
        Assert.Equal([episodeIds[0]], await database.GetDisabledItemIdsAsync(seasonId));

        // B's re-projection withdrew the automatic row A's failed write left behind.
        Assert.Empty(await store.GetOwnSegmentsAsync(episodeIds[0], CancellationToken.None));
    }

    [Fact]
    public async Task DisabledItems_ConcurrentSegmentCreateSerializesBehindFailingProjection()
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
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
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
        var controller = CreateController(
            loggerFactory, database, pluginScope.CacheDbPath, segmentChange);
        var segmentsController = new SegmentsController(database, segmentChange);

        // Request A commits the disable flag, then parks inside its projection write
        // while holding the item's mutation stripe.
        var requestA = controller.DisableItem(episodeIds[0], CancellationToken.None);
        await writeEntered.Task;

        // A segment create for the same item must serialize behind the stripe: a
        // create interleaving with A's projection would race the mirror push.
        var requestB = segmentsController.CreateSegment(
            episodeIds[0], new CreateSegmentRequest(AnalysisMode.Commercial, 100, 120), CancellationToken.None);
        Assert.NotSame(requestB, await Task.WhenAny(requestB, Task.Delay(250)));
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
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], updateMediaSegments: true);
        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", movieId);
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_libraryManager",
            EntrypointTestHelpers.CreateLibraryManager(movie));
        using var loggerFactory = LoggerFactory.Create(builder => { });
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(loggerFactory, database, pluginScope.CacheDbPath);

        var result = await controller.DisableItem(movieId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);

        // The server records the movie's own ID as its season key, which is what
        // the dashboard's movie view lists by.
        Assert.Equal([movieId], await database.GetDisabledItemIdsAsync(movieId));
    }

    private static Episode CreateEpisodeItem(Guid episodeId, Guid seasonId)
    {
        var episode = new Episode();
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeasonId", seasonId);
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

    private static VisualizationController CreateController(ILoggerFactory loggerFactory, string dbPath, string cacheDbPath)
        => CreateController(loggerFactory, DatabaseTestHelpers.CreateSegmentDatabase(dbPath), cacheDbPath);

    private static VisualizationController CreateController(ILoggerFactory loggerFactory, IntroSkipperDatabase database, string cacheDbPath, IntroSkipper.SegmentChanges.SegmentChange? segmentChange = null)
    {
        return new VisualizationController(
            NullLogger<VisualizationController>.Instance,
            segmentChange ?? DatabaseTestHelpers.CreateSegmentChange(new FakeJellyfinSegmentStore(), database),
            libraryManager: null!,
            new AnalyzerTaskFactory(
                loggerFactory,
                libraryManager: null!,
                providerManager: null!,
                fileSystem: null!,
                ffmpegService: null!,
                DatabaseTestHelpers.CreateCacheService(cacheDbPath),
                database),
            database,
            DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath),
            EntrypointTestHelpers.CreateTaskManager());
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
            new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default),
            new DbSeasonState(seasonId, AnalysisMode.Credits, AnalyzerAction.Default));
        db.AnalyzedItems.AddRange(episodeIds.SelectMany(id => new[]
        {
            new DbAnalyzedItem(id, AnalysisMode.Introduction, "hash"),
            new DbAnalyzedItem(id, AnalysisMode.Credits, "hash")
        }));
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
        => DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-visualization-controller.db");

}
