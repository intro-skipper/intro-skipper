using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestSkipIntroController
{
    [Fact]
    public async Task UpdateTimestampsAsync_AwaitsDirectMediaSegmentRefresh_BeforeReturningNoContent()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(itemId, updateMediaSegments: true, out var item);
        await EnsureDatabaseAsync(dbPath);
        var refresher = new RecordingMediaSegmentRefresher
        {
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        // Pre-warm the facade's init gate so the action below runs synchronously up to the
        // refresher await (its single pending point). With a cold gate, initialization
        // completes on the thread pool and the pre-completion assertions race it. This
        // mirrors production, where the hosted initializer warms the gate before traffic.
        var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
        await database.InitializeAsync();
        var controller = new SkipIntroController(
            refresher,
            DatabaseTestHelpers.CreateCacheDatabase(pluginScope.CacheDbPath),
            database);
        var timestamps = new TimeStamps
        {
            Introduction = new Segment(itemId, new TimeRange(10, 20))
        };

        var actionTask = controller.UpdateTimestampsAsync(itemId, timestamps, CancellationToken.None);

        Assert.False(actionTask.IsCompleted);
        Assert.Equal(1, refresher.ItemCallCount);
        Assert.Equal(item.Id, refresher.LastItemId);

        refresher.Completion.SetResult();
        var result = await actionTask;

        Assert.IsType<NoContentResult>(result);
        await using var db = new IntroSkipperDbContext(dbPath);
        var segment = await db.Segments.SingleAsync();
        Assert.Equal(itemId, segment.ItemId);
        Assert.Equal(AnalysisMode.Introduction, segment.Type);
        Assert.True(segment.IsUserProvided);
    }

    [Fact]
    public async Task UpdateTimestampsAsync_AlwaysDelegatesRefresh_TheServiceOwnsTheMirrorFlag()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(itemId, updateMediaSegments: false, out _);
        await EnsureDatabaseAsync(dbPath);
        var refresher = new RecordingMediaSegmentRefresher();
        var controller = new SkipIntroController(
            refresher,
            DatabaseTestHelpers.CreateCacheDatabase(pluginScope.CacheDbPath),
            DatabaseTestHelpers.CreateSegmentDatabase(dbPath));
        var timestamps = new TimeStamps
        {
            Introduction = new Segment(itemId, new TimeRange(10, 20))
        };

        var result = await controller.UpdateTimestampsAsync(itemId, timestamps, CancellationToken.None);

        // The controller no longer gates on UpdateMediaSegments: it always delegates,
        // and MediaSegmentRefreshService itself owns the mirror flag.
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, refresher.ItemCallCount);
    }

    [Fact]
    public async Task UpdateTimestampsAsync_CommercialSlot_ReplacesAllStoredCommercials()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(itemId, updateMediaSegments: false, out _);
        await EnsureDatabaseAsync(dbPath);
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
        var controller = new SkipIntroController(
            new RecordingMediaSegmentRefresher(),
            DatabaseTestHelpers.CreateCacheDatabase(pluginScope.CacheDbPath),
            database);
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
        Assert.True(commercial.IsUserProvided);
        var intro = Assert.Single(rows, row => row.Type == AnalysisMode.Introduction);
        Assert.Equal(TickConversions.FromSeconds(10), intro.StartTicks);
        Assert.False(intro.IsUserProvided);
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
        var controller = new SkipIntroController(
            new RecordingMediaSegmentRefresher(),
            DatabaseTestHelpers.CreateCacheDatabase(missingCachePath),
            database);

        var result = await controller.ResetIntroTimestamps(
            AnalysisMode.Introduction,
            eraseCache: true,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
    }

    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope(Guid itemId, bool updateMediaSegments, out Movie item)
    {
        var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        item = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(item, "Id", itemId);
        EntrypointTestHelpers.EnsureNonVirtual(item);

        var libraryManager = EntrypointTestHelpers.CreateLibraryManager(item);
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration { UpdateMediaSegments = updateMediaSegments });
        EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", libraryManager);
        return scope;
    }

    private static async Task EnsureDatabaseAsync(string dbPath)
    {
        await using var db = new IntroSkipperDbContext(dbPath);
        await db.ApplyMigrationsAsync();
    }

    private static string CreateTempDbPath()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "skip-controller");
        Directory.CreateDirectory(tempDir);
        return Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");
    }

    private sealed class RecordingMediaSegmentRefresher : IMediaSegmentRefresher
    {
        public TaskCompletionSource? Completion { get; init; }

        public int ItemCallCount { get; private set; }

        public Guid LastItemId { get; private set; }

        public Task RefreshAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            ItemCallCount++;
            LastItemId = item.Id;
            return Completion?.Task ?? Task.CompletedTask;
        }

        public Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
        {
            return Completion?.Task ?? Task.CompletedTask;
        }

        public Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
            => RefreshAsync(itemIds, cancellationToken);
    }
}
