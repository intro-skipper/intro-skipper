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
        var controller = new SkipIntroController(refresher, DatabaseTestHelpers.CreateCacheDatabase(pluginScope.CacheDbPath), DatabaseTestHelpers.CreateSegmentDatabase(dbPath));
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
        var segment = await db.DbSegment.SingleAsync();
        Assert.Equal(itemId, segment.ItemId);
        Assert.Equal(AnalysisMode.Introduction, segment.Type);
        Assert.True(segment.IsUserProvided);
    }

    [Fact]
    public async Task UpdateTimestampsAsync_DoesNotRefresh_WhenUpdateMediaSegmentsDisabled()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = CreatePluginScope(itemId, updateMediaSegments: false, out _);
        await EnsureDatabaseAsync(dbPath);
        var refresher = new RecordingMediaSegmentRefresher
        {
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var controller = new SkipIntroController(refresher, DatabaseTestHelpers.CreateCacheDatabase(pluginScope.CacheDbPath), DatabaseTestHelpers.CreateSegmentDatabase(dbPath));
        var timestamps = new TimeStamps
        {
            Introduction = new Segment(itemId, new TimeRange(10, 20))
        };

        var result = await controller.UpdateTimestampsAsync(itemId, timestamps, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, refresher.ItemCallCount);
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
        await db.Database.EnsureCreatedAsync();
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
    }
}
