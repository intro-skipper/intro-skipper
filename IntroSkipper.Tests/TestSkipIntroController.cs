using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestSkipIntroController
{
    [Fact]
    public async Task UpdateTimestampsAsync_AwaitsMirrorWrite_BeforeReturningNoContent()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJellyfinSegmentStore
        {
            WriteEntered = writeEntered,
            WriteGate = writeGate,
            BlockedItemId = itemId
        };
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(store, database, pluginScope.CacheDbPath);
        var timestamps = new TimeStamps
        {
            Introduction = new Segment(itemId, new TimeRange(10, 20))
        };

        var actionTask = controller.UpdateTimestampsAsync(itemId, timestamps, CancellationToken.None);

        // The plugin row is committed and the mirror write has started; the response
        // still has to wait for that write to finish.
        await writeEntered.Task;
        Assert.False(actionTask.IsCompleted);

        writeGate.SetResult();
        var result = await actionTask;

        Assert.IsType<NoContentResult>(result);
        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        var segment = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(AnalysisMode.Introduction, segment.Type);
        Assert.Equal(SegmentSource.User, segment.Source);
        Assert.Equal(segment.Id, Assert.Single(pushed).Id);
    }

    [Fact]
    public async Task UpdateTimestampsAsync_MirrorDisabled_StoresSegmentWithoutJellyfinWrite()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: false, out _);
        var store = new FakeJellyfinSegmentStore();
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(store, database, pluginScope.CacheDbPath);
        var timestamps = new TimeStamps
        {
            Introduction = new Segment(itemId, new TimeRange(10, 20))
        };

        var result = await controller.UpdateTimestampsAsync(itemId, timestamps, CancellationToken.None);

        // The controller does not gate on UpdateMediaSegments; the mirror itself no-ops.
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, store.WriteCallCount);
        var segment = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.User, segment.Source);
    }

    [Fact]
    public async Task UpdateTimestampsAsync_MirrorFailure_PropagatesAndKeepsStoredSegment()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var store = new FakeJellyfinSegmentStore
        {
            WriteException = new InvalidOperationException("boom")
        };
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        var controller = CreateController(store, database, pluginScope.CacheDbPath);
        var timestamps = new TimeStamps
        {
            Introduction = new Segment(itemId, new TimeRange(10, 20))
        };

        // Like every editor write: the failure reaches the caller instead of a 204, and
        // the committed plugin row stays (the next sync converges the mirror).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.UpdateTimestampsAsync(itemId, timestamps, CancellationToken.None));

        Assert.Equal(1, store.WriteCallCount);
        var segment = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.User, segment.Source);
    }

    [Fact]
    public async Task UpdateTimestampsAsync_CommercialSlot_ReplacesAllStoredCommercials()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: false, out _);
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Commercial,
            [new Segment(itemId, new TimeRange(300, 330)), new Segment(itemId, new TimeRange(600, 630))],
            SegmentSource.BlackFrame);
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(10, 20))],
            SegmentSource.Chapter);
        var controller = CreateController(new FakeJellyfinSegmentStore(), database, pluginScope.CacheDbPath);
        var timestamps = new TimeStamps
        {
            Commercial = new Segment(itemId, new TimeRange(400, 430))
        };

        var result = await controller.UpdateTimestampsAsync(itemId, timestamps, CancellationToken.None);

        // The deprecated singular endpoint replaces every stored commercial with the one
        // posted user segment (no more append), while other modes stay untouched.
        Assert.IsType<NoContentResult>(result);
        var rows = await database.GetSegmentsAsync(itemId);
        var commercial = Assert.Single(rows, row => row.Type == AnalysisMode.Commercial);
        Assert.Equal(TickConversions.FromSeconds(400), commercial.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(430), commercial.EndTicks);
        Assert.Equal(SegmentSource.User, commercial.Source);
        var intro = Assert.Single(rows, row => row.Type == AnalysisMode.Introduction);
        Assert.Equal(TickConversions.FromSeconds(10), intro.StartTicks);
        Assert.NotEqual(SegmentSource.User, intro.Source);
    }

    [Fact]
    public async Task ResetIntroTimestamps_CacheFailureDoesNotFailMainDatabaseDelete()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(10, 20))],
            SegmentSource.Chapter);
        var missingCachePath = Path.Join(
            Path.GetTempPath(),
            "IntroSkipper.Tests",
            "skip-controller",
            Guid.NewGuid().ToString("N"),
            "cache.db");
        var refresher = new RecordingMediaSegmentRefresher();
        var controller = CreateController(new FakeJellyfinSegmentStore(), database, missingCachePath, refresher);

        var result = await controller.ResetIntroTimestamps(
            AnalysisMode.Introduction,
            eraseCache: true,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
        // The erased items' mirrors are converged so Jellyfin stops serving the rows.
        Assert.Equal([itemId], refresher.RefreshedItemIds);
    }

    [Fact]
    public async Task RebuildDatabase_UnreadableBackup_Returns409_AndForceRebuildsClean()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            // A garbage file makes both initialization and the backup read fail.
            await File.WriteAllTextAsync(dbPath, "this is not a sqlite database file");
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var controller = CreateController(new FakeJellyfinSegmentStore(), database, DatabaseTestHelpers.CreateTempCacheDbPath());

            // Without force the endpoint reports the unreadable backup as a conflict,
            // so the dashboard can ask for explicit consent instead of losing data.
            Assert.IsType<ConflictObjectResult>(await controller.RebuildDatabase());

            // With force the unreadable file is discarded and the rebuild succeeds;
            // the facade is operational over the recreated database.
            Assert.IsType<NoContentResult>(await controller.RebuildDatabase(forceCleanOnBackupFailure: true));
            Assert.Empty(await database.GetSegmentsAsync(Guid.NewGuid()));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    private static SkipIntroController CreateController(
        IJellyfinSegmentStore store,
        IntroSkipperDatabase database,
        string cacheDbPath,
        IMediaSegmentRefresher? refresher = null)
        => new(
            DatabaseTestHelpers.CreateEditorService(store, database),
            refresher ?? new RecordingMediaSegmentRefresher(),
            DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath),
            database);

    private static string CreateTempDbPath()
        => DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-skip-controller.db");

    private sealed class RecordingMediaSegmentRefresher : IMediaSegmentRefresher
    {
        public IReadOnlyList<Guid> RefreshedItemIds { get; private set; } = [];

        public Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
        {
            RefreshedItemIds = [.. itemIds];
            return Task.CompletedTask;
        }

        public Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
