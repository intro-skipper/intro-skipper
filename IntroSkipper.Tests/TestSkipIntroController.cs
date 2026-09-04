using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestSkipIntroController : IDisposable
{
    private readonly SegmentChangeHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task UpdateTimestampsAsync_AwaitsMirrorWrite_BeforeReturningNoContent()
    {
        var itemId = Guid.NewGuid();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Store = new FakeJellyfinSegmentStore
        {
            WriteEntered = writeEntered,
            WriteGate = writeGate,
            BlockedItemId = itemId
        };
        var controller = CreateController(pluginScope.CacheDbPath);
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
        var (replacedItemId, pushed) = Assert.Single(_h.Store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        var segment = Assert.Single(await _h.Database.GetSegmentsAsync(itemId));
        Assert.Equal(AnalysisMode.Introduction, segment.Type);
        Assert.Equal(SegmentSource.User, segment.Source);
        Assert.Equal(segment.Id, Assert.Single(pushed).Id);
    }

    [Fact]
    public async Task UpdateTimestampsAsync_MirrorFailure_ReportsAcceptedPending_AndKeepsStoredSegment()
    {
        var itemId = Guid.NewGuid();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        _h.Store = new FakeJellyfinSegmentStore
        {
            WriteException = new InvalidOperationException("boom")
        };
        var controller = CreateController(pluginScope.CacheDbPath);
        var timestamps = new TimeStamps
        {
            Introduction = new Segment(itemId, new TimeRange(10, 20))
        };

        // The committed change stands; the failed mirror write surfaces as an
        // accepted-plus-pending 202 and the journaled work retries until convergence.
        var result = await controller.UpdateTimestampsAsync(itemId, timestamps, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal("Pending", Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value).Projection);
        Assert.Equal(1, _h.Store.WriteCallCount);
        var segment = Assert.Single(await _h.Database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.User, segment.Source);
    }

    [Fact]
    public async Task UpdateTimestampsAsync_CommercialSlot_ReplacesAllStoredCommercials()
    {
        var itemId = Guid.NewGuid();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = _h.Database;
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
        var controller = CreateController(pluginScope.CacheDbPath);
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
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(10, 20))],
            SegmentSource.Chapter);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        // The row is mirrored; the erase must converge it away despite the cache failure.
        _h.Store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, row.Id, MediaSegmentType.Intro, row.StartTicks, row.EndTicks)],
        };
        var controller = CreateController(SegmentChangeHarness.MissingCachePath());

        var result = await controller.ResetIntroTimestamps(
            AnalysisMode.Introduction,
            eraseCache: true,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
        // The erase journaled the item's projection and the request converged it, so
        // Jellyfin stops serving the rows.
        Assert.Empty(await _h.Store.GetOwnSegmentsAsync(itemId, CancellationToken.None));
    }

    [Fact]
    public async Task RebuildDatabase_UnreadableBackup_Returns409_AndForceRebuildsClean()
    {
        // A garbage file makes both initialization and the backup read fail.
        await File.WriteAllTextAsync(_h.DbPath, "this is not a sqlite database file");
        var controller = CreateController(DatabaseTestHelpers.CreateTempCacheDbPath());

        // Without force the endpoint reports the unreadable backup as a conflict,
        // so the dashboard can ask for explicit consent instead of losing data.
        Assert.IsType<ConflictObjectResult>(await controller.RebuildDatabase());

        // With force the unreadable file is discarded and the rebuild succeeds;
        // the facade is operational over the recreated database.
        Assert.IsType<NoContentResult>(await controller.RebuildDatabase(forceCleanOnBackupFailure: true));
        Assert.Empty(await _h.Database.GetSegmentsAsync(Guid.NewGuid()));
    }

    private SkipIntroController CreateController(string cacheDbPath)
        => new(_h.Change, DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath), _h.Database);
}
