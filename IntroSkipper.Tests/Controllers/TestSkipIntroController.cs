// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestSkipIntroController : IDisposable
{
    private readonly SegmentChangeHarness _h = new();

    public void Dispose() => _h.Dispose();

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
            cancellationToken: CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
        // The erase journaled the item's projection and the request converged it, so
        // Jellyfin stops serving the rows.
        Assert.Empty(await _h.Store.GetOwnSegmentsAsync(itemId, CancellationToken.None));
    }

    [Fact]
    public async Task ResetIntroTimestamps_KeepsUserSegments_InTheDatabaseAndTheMirror()
    {
        var itemId = Guid.NewGuid();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(10, 20))],
            SegmentSource.Chapter);
        var user = await database.SeedUserSegmentAsync(itemId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(100), DatabaseTestHelpers.Ticks(130));

        // Jellyfin holds both rows, as the mirror pushed them.
        _h.Store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [.. (await database.GetSegmentsAsync(itemId)).Select(row => SegmentChangeHarness.MirroredDto(itemId, row.Id, MediaSegmentType.Intro, row.StartTicks, row.EndTicks))],
        };
        var controller = CreateController(DatabaseTestHelpers.CreateTempCacheDbPath());

        var result = await controller.ResetIntroTimestamps(
            AnalysisMode.Introduction,
            eraseCache: false,
            cancellationToken: CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(user.Id, Assert.Single(await database.GetSegmentsAsync(itemId)).Id);
        Assert.Equal(user.Id, Assert.Single(await _h.Store.GetOwnSegmentsAsync(itemId, CancellationToken.None)).Id);
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
        => new(new SegmentEraser(_h.Database, DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath), _h.Change), _h.Database);
}
