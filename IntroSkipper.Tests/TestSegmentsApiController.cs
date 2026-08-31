// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using static IntroSkipper.Tests.DatabaseTestHelpers;

namespace IntroSkipper.Tests;

/// <summary>
/// The plural segments API (<c>Episode/{itemId}/Segments</c>): id-addressed CRUD +
/// restore over the redesigned store, pushing to Jellyfin via the uniform item sync.
/// </summary>
public sealed class TestSegmentsApiController
{
    [Fact]
    public async Task GetSegments_ReturnsAllSegments_WithSecondsAndSource()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Introduction,
                [new Segment(itemId, new TimeRange(10, 60)), new Segment(itemId, new TimeRange(200, 260))],
                SegmentSource.Chromaprint);
            await database.AddUserSegmentAsync(itemId, AnalysisMode.Commercial, Ticks(100), Ticks(120));

            var controller = CreateController(database, out _);
            var response = await controller.GetSegments(itemId, cancellationToken: CancellationToken.None);

            var dtos = Assert.IsAssignableFrom<IReadOnlyList<SegmentDto>>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.Equal(3, dtos.Count);

            var intros = dtos.Where(d => d.Type == AnalysisMode.Introduction).ToList();
            Assert.Equal(2, intros.Count);
            Assert.Equal(10, intros[0].Start);
            Assert.Equal(60, intros[0].End);
            Assert.Equal(SegmentSource.Chromaprint, intros[0].Source);

            var commercial = Assert.Single(dtos, d => d.Type == AnalysisMode.Commercial);
            Assert.Equal(SegmentSource.User, commercial.Source);
            Assert.False(commercial.Suppressed);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetSegments_ReturnsEmptyList_ForValidItemWithoutSegments_And404ForUnknownItem()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var controller = CreateController(DatabaseTestHelpers.CreateSegmentDatabase(dbPath), out _);

            var empty = await controller.GetSegments(itemId, cancellationToken: CancellationToken.None);
            var dtos = Assert.IsAssignableFrom<IReadOnlyList<SegmentDto>>(Assert.IsType<OkObjectResult>(empty.Result).Value);
            Assert.Empty(dtos);

            var missing = await controller.GetSegments(Guid.NewGuid(), cancellationToken: CancellationToken.None);
            Assert.IsType<NotFoundResult>(missing.Result);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CreateSegment_PersistsUserRow_AndPushesUniformSync()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var controller = CreateController(database, out var store);

            var response = await controller.CreateSegment(
                itemId,
                new CreateSegmentRequest(AnalysisMode.Commercial, 100, 120),
                CancellationToken.None);

            var dto = Assert.IsType<SegmentDto>(Assert.IsType<CreatedAtActionResult>(response.Result).Value);
            Assert.Equal(AnalysisMode.Commercial, dto.Type);
            Assert.Equal(SegmentSource.User, dto.Source);
            Assert.NotEqual(Guid.Empty, dto.Id);

            var stored = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.Equal(dto.Id, stored.Id);

            // Uniform mirror push: the replaced set carries the plugin row id.
            var pushed = Assert.Single(store.ReplacedItems);
            Assert.Equal(itemId, pushed.ItemId);
            var pushedDto = Assert.Single(pushed.Segments);
            Assert.Equal(dto.Id, pushedDto.Id);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CreateSegment_DoesNotPush_WhenUpdateMediaSegmentsDisabled()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: false, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var controller = CreateController(database, out var store);

            var response = await controller.CreateSegment(
                itemId,
                new CreateSegmentRequest(AnalysisMode.Introduction, 5, 30),
                CancellationToken.None);

            // The segment committed, but mirroring is off: the response reports the
            // skipped projection honestly and the journaled work replays on enable.
            var accepted = Assert.IsType<AcceptedResult>(response.Result);
            var body = Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value);
            Assert.Equal("Skipped", body.Projection);
            Assert.Equal(AnalysisMode.Introduction, Assert.Single(body.Segments).Type);
            Assert.Empty(store.ReplacedItems);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Theory]
    [InlineData(30.0, 30.0)]  // zero-length
    [InlineData(30.0, 20.0)]  // end before start
    [InlineData(-5.0, 20.0)]  // negative start
    public async Task CreateSegment_BadRequest_ForInvalidBoundaries(double start, double end)
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var controller = CreateController(DatabaseTestHelpers.CreateSegmentDatabase(dbPath), out _);

            var response = await controller.CreateSegment(
                itemId,
                new CreateSegmentRequest(AnalysisMode.Introduction, start, end),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(response.Result);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public void CreateSegmentRequest_RejectsOmittedType_InsteadOfDefaultingToIntroduction()
    {
        // A positional record binds an omitted property to default(AnalysisMode) =
        // Introduction; the request marks Type required so the body is rejected (400)
        // instead of silently creating an intro.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateSegmentRequest>("""{"Start":10,"End":20}"""));

        var request = JsonSerializer.Deserialize<CreateSegmentRequest>("""{"Type":"Credits","Start":10,"End":20}""");
        Assert.Equal(AnalysisMode.Credits, request!.Type);
    }

    [Fact]
    public async Task CreateSegment_BadRequest_ForUndefinedNumericMode()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var controller = CreateController(database, out var store);

            // The enum-string converter also binds raw integers, so an undefined numeric
            // mode reaches the action; it must be rejected before the database write —
            // a committed row would fail every later mirror of the item.
            var response = await controller.CreateSegment(
                itemId,
                new CreateSegmentRequest((AnalysisMode)999, 10, 20),
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(response.Result);
            Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
            Assert.Empty(store.ReplacedItems);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UpdateSegment_MovesBoundaries_404ForWrongItem_MergesOnExactCollision()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Commercial,
                [new Segment(itemId, new TimeRange(10, 20)), new Segment(itemId, new TimeRange(50, 60))],
                SegmentSource.Chapter);
            var rows = await database.GetSegmentsAsync(itemId);
            var controller = CreateController(database, out _);

            var updated = await controller.UpdateSegment(itemId, rows[0].Id, new UpdateSegmentRequest(12, 22), CancellationToken.None);
            var dto = Assert.IsType<SegmentDto>(Assert.IsType<OkObjectResult>(updated.Result).Value);
            Assert.Equal(12, dto.Start);
            Assert.Equal(SegmentSource.User, dto.Source);

            var wrongItem = await controller.UpdateSegment(Guid.NewGuid(), rows[0].Id, new UpdateSegmentRequest(1, 2), CancellationToken.None);
            Assert.IsType<NotFoundResult>(wrongItem.Result);

            // Moving exactly onto the sibling merges into it: the occupant survives as
            // the returned user segment and the moved row is absorbed.
            var merged = await controller.UpdateSegment(itemId, rows[0].Id, new UpdateSegmentRequest(50, 60), CancellationToken.None);
            var mergedDto = Assert.IsType<SegmentDto>(Assert.IsType<OkObjectResult>(merged.Result).Value);
            Assert.Equal(rows[1].Id, mergedDto.Id);
            Assert.Equal(SegmentSource.User, mergedDto.Source);
            var survivor = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
            Assert.Equal(rows[1].Id, survivor.Id);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteSegment_TombstonesAuto_HardDeletesUser_AndDeletesJellyfinRowById()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);
            var autoRow = Assert.Single(await database.GetSegmentsAsync(itemId));
            var userRow = await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));
            var controller = CreateController(database, out var store);

            Assert.IsType<NoContentResult>(await controller.DeleteSegment(itemId, autoRow.Id, CancellationToken.None));
            var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true), s => s.Id == autoRow.Id);
            Assert.Equal(SegmentState.Suppressed, tombstone.State);

            // The projection converges the item's whole mirror: only the surviving
            // user row remains pushed.
            Assert.Equal(userRow.Id, Assert.Single(await store.GetOwnSegmentsAsync(itemId, CancellationToken.None)).Id);

            Assert.IsType<NoContentResult>(await controller.DeleteSegment(itemId, userRow.Id, CancellationToken.None));
            Assert.DoesNotContain(await database.GetSegmentsAsync(itemId, includeSuppressed: true), s => s.Id == userRow.Id);
            Assert.Empty(await store.GetOwnSegmentsAsync(itemId, CancellationToken.None));

            Assert.IsType<NotFoundResult>(await controller.DeleteSegment(itemId, Guid.NewGuid(), CancellationToken.None));
            Assert.IsType<NotFoundResult>(await controller.DeleteSegment(itemId, autoRow.Id, CancellationToken.None)); // already suppressed
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteSegment_DoesNotTouchJellyfin_WhenUpdateMediaSegmentsDisabled()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: false, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);
            var row = Assert.Single(await database.GetSegmentsAsync(itemId));
            var controller = CreateController(database, out var store);

            var response = await controller.DeleteSegment(itemId, row.Id, CancellationToken.None);

            // Plugin-side delete committed; Jellyfin stays untouched and the response
            // reports the skipped projection honestly (the journaled work replays on
            // enable), consistent with how create/update/restore honor the flag.
            var accepted = Assert.IsType<AcceptedResult>(response);
            Assert.Equal("Skipped", Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value).Projection);
            var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
            Assert.Equal(SegmentState.Suppressed, tombstone.State);
            Assert.Empty(store.DeletedSegments);
            Assert.Empty(store.ReplacedItems);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteSegment_KeepsTombstone_WhenJellyfinProjectionFails()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint, "cfg");
            var row = Assert.Single(await database.GetSegmentsAsync(itemId));

            // The row is mirrored, and the convergence write fails: the committed
            // tombstone must stand (202, not an error, no rollback) with the
            // journaled work retrying until Jellyfin converges.
            var store = new FakeJellyfinSegmentStore
            {
                ExistingSegments =
                [
                    new MediaBrowser.Model.MediaSegments.MediaSegmentDto
                    {
                        Id = row.Id,
                        ItemId = itemId,
                        Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                        StartTicks = row.StartTicks,
                        EndTicks = row.EndTicks,
                    }
                ],
                WriteException = new InvalidOperationException("Jellyfin unavailable"),
            };
            var controller = CreateController(database, store);

            var response = await controller.DeleteSegment(itemId, row.Id, CancellationToken.None);

            var accepted = Assert.IsType<AcceptedResult>(response);
            Assert.Equal("Pending", Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value).Projection);
            var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
            Assert.Equal(row.Id, tombstone.Id);
            Assert.Equal(SegmentState.Suppressed, tombstone.State);
            Assert.Equal("cfg", tombstone.ConfigHash);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RestoreSegment_ReactivatesTombstone_404Otherwise()
    {
        var itemId = Guid.NewGuid();
        var dbPath = CreateTempDbPath();
        using var pluginScope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.BlackFrame);
            var row = Assert.Single(await database.GetSegmentsAsync(itemId));
            await database.DeleteSegmentAsync(itemId, row.Id);
            var controller = CreateController(database, out _);

            var restored = await controller.RestoreSegment(itemId, row.Id, CancellationToken.None);
            var dto = Assert.IsType<SegmentDto>(Assert.IsType<OkObjectResult>(restored.Result).Value);
            Assert.False(dto.Suppressed);
            Assert.Equal(SegmentSource.BlackFrame, dto.Source);

            // Not suppressed anymore → 404; unknown id → 404.
            Assert.IsType<NotFoundResult>((await controller.RestoreSegment(itemId, row.Id, CancellationToken.None)).Result);
            Assert.IsType<NotFoundResult>((await controller.RestoreSegment(itemId, Guid.NewGuid(), CancellationToken.None)).Result);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    private static SegmentsController CreateController(
        IntroSkipperDatabase database,
        out FakeJellyfinSegmentStore store)
    {
        store = new FakeJellyfinSegmentStore();
        return CreateController(database, store);
    }

    private static SegmentsController CreateController(IntroSkipperDatabase database, FakeJellyfinSegmentStore store)
        => new(database, DatabaseTestHelpers.CreateSegmentChange(store, database));

    private static string CreateTempDbPath()
        => DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-segments-api.db");
}
