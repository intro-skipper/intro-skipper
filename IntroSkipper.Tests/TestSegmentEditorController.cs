// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using static IntroSkipper.Tests.DatabaseTestHelpers;

/// <summary>
/// Tests for <see cref="SegmentEditorController"/> over the durable segment-change
/// coordinator: the legacy delete dispatch (shared-id fast path, uncorrelated exact
/// fallback with one tick of tolerance and the non-commercial mode-wide fallback),
/// validation wire behavior, and accepted-plus-pending semantics when the Jellyfin
/// projection cannot apply synchronously. The facade-level rules behind each dispatch
/// branch are pinned in <c>TestSegmentChange</c>; these tests cover the HTTP mapping.
/// </summary>
public sealed class SegmentEditorControllerTests : IDisposable
{
    private readonly SegmentChangeHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task DeleteSegment_KeepsTombstone_WhenUncorrelatedJellyfinDeleteFails()
    {
        using var scope = CreateScope();
        var itemId = Guid.NewGuid();
        var jellyfinSegmentId = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(100, 160))],
            SegmentSource.Chapter,
            "cfg-2");
        var original = Assert.Single(await database.GetSegmentsAsync(itemId));

        // The Jellyfin row's id matches no plugin row (it predates the shared-id
        // scheme), so the plugin counterpart is matched by exact ticks and
        // tombstoned. The failing Jellyfin delete no longer rolls that back: the
        // change reports accepted-plus-pending and the journaled foreign-row delete
        // retries until Jellyfin converges.
        _h.Store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, jellyfinSegmentId, MediaSegmentType.Intro, Ticks(100), Ticks(160))],
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController();

        var result = await controller.DeleteSegmentAsync(jellyfinSegmentId, itemId, "intro", CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value);
        Assert.Equal("Pending", body.Projection);
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(original.Id, tombstone.Id);
        Assert.Equal(SegmentState.Suppressed, tombstone.State);
        Assert.Empty(_h.Store.DeletedSegments);
    }

    [Theory]
    [InlineData(MediaSegmentType.Outro)]
    [InlineData((MediaSegmentType)int.MaxValue)]
    public async Task DeleteSegment_RejectsMismatchedOrUnsupportedExistingSegmentType_WithoutMutatingEitherStore(
        MediaSegmentType existingType)
    {
        using var scope = CreateScope();
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(100, 160))], SegmentSource.Chapter, "cfg-intro");
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(1200, 1260))], SegmentSource.Chapter, "cfg-credits");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [itemId], "cfg-intro");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [itemId], "cfg-credits");

        _h.Store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, segmentId, existingType, Ticks(1200), Ticks(1260))],
        };
        var controller = CreateController();

        var response = await controller.DeleteSegmentAsync(segmentId, itemId, "intro", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response);
        Assert.Empty(_h.Store.DeletedSegments);

        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, rows.Count);
        var intro = Assert.Single(rows, row => row.Type == AnalysisMode.Introduction);
        Assert.Equal(TickConversions.FromSeconds(100), intro.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(160), intro.EndTicks);
        Assert.Equal("cfg-intro", intro.ConfigHash);
        var credits = Assert.Single(rows, row => row.Type == AnalysisMode.Credits);
        Assert.Equal(TickConversions.FromSeconds(1200), credits.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(1260), credits.EndTicks);
        Assert.Equal("cfg-credits", credits.ConfigHash);

        var snapshot = await database.GetSeasonQueueSnapshotAsync(itemId, [itemId]);
        Assert.Equal("cfg-intro", snapshot.AnalyzedConfigHashes[(itemId, AnalysisMode.Introduction)]);
        Assert.Equal("cfg-credits", snapshot.AnalyzedConfigHashes[(itemId, AnalysisMode.Credits)]);
    }

    [Fact]
    public async Task DeleteSegment_RemovesPluginRow_WhenJellyfinDeleteSucceeds_AndJellyfinSegmentAlreadyGone()
    {
        using var scope = CreateScope();
        var itemId = Guid.NewGuid();
        var database = _h.Database;
        var row = await database.SeedUserSegmentAsync(
            itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(100), TickConversions.FromSeconds(160));

        // Jellyfin segment already gone; the correlated delete commits and the
        // convergence finds nothing left to change, so the orphaned user row is
        // cleaned up as a plain synchronous success.
        var store = _h.Store;
        var controller = CreateController();

        var result = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Empty(await _h.Store.GetOwnSegmentsAsync(itemId, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSegment_TombstonesAutomaticSegment_AndSecondDeleteSucceedsIdempotently()
    {
        using var scope = CreateScope();
        var itemId = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(100, 160))], SegmentSource.Chapter, "cfg-auto");
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        var store = _h.Store;
        var controller = CreateController();

        var result = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);

        // The automatic row survives as a tombstone so re-analysis cannot re-add it.
        Assert.IsType<OkResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(row.Id, tombstone.Id);
        Assert.Equal(SegmentState.Suppressed, tombstone.State);
        Assert.Equal(SegmentSource.Chapter, tombstone.Source);
        Assert.Empty(await _h.Store.GetOwnSegmentsAsync(itemId, CancellationToken.None));

        // Deleting the already-suppressed row again succeeds idempotently (the plugin
        // already treats it as deleted) instead of 404ing.
        var second = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);
        Assert.IsType<OkResult>(second);
    }

    [Fact]
    public async Task DeleteSegment_GhostJellyfinRow_ConvergesMirrorAndSucceeds()
    {
        using var scope = CreateScope();
        var itemId = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(100, 160))], SegmentSource.Chapter, "cfg-auto");
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.NotNull(await database.DeleteSegmentAsync(itemId, row.Id));

        // Jellyfin re-added the row from a provider read predating the delete (the
        // mirror's documented race); the user deletes the visible ghost again.
        _h.Store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, row.Id, MediaSegmentType.Intro, row.StartTicks, row.EndTicks)],
        };
        var controller = CreateController();

        var result = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);

        // The delete converges the item's mirror instead of 404ing while the ghost
        // keeps serving; the tombstone stays so re-analysis cannot re-add the range.
        Assert.IsType<OkResult>(result);
        Assert.Empty(await _h.Store.GetOwnSegmentsAsync(itemId, CancellationToken.None));
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(row.Id, tombstone.Id);
        Assert.Equal(SegmentState.Suppressed, tombstone.State);
    }

    [Fact]
    public async Task DeleteSegment_DriftedRow_KeepsExactMatching_WhenModeHasMultipleActiveRows()
    {
        using var scope = CreateScope();
        var itemId = Guid.NewGuid();
        var jellyfinRowId = Guid.NewGuid();
        var database = _h.Database;
        await database.SeedUserSegmentAsync(
            itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(10), TickConversions.FromSeconds(60));
        await database.SeedUserSegmentAsync(
            itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(100), TickConversions.FromSeconds(160));

        // Two active intros make the mode-wide fallback ambiguous: only the named
        // Jellyfin row is removed, neither plugin row is guessed at.
        _h.Store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, jellyfinRowId, MediaSegmentType.Intro, Ticks(200), Ticks(260))],
        };
        var controller = CreateController();

        var result = await controller.DeleteSegmentAsync(jellyfinRowId, itemId, "intro", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Equal(2, (await database.GetSegmentsAsync(itemId)).Count);
        Assert.Equal([(itemId, jellyfinRowId)], _h.Store.DeletedSegments);
    }

    [Fact]
    public async Task DeleteSegment_DriftedCommercialRow_KeepsExactMatching()
    {
        using var scope = CreateScope();
        var itemId = Guid.NewGuid();
        var jellyfinRowId = Guid.NewGuid();
        var database = _h.Database;
        var commercial = await database.SeedUserSegmentAsync(
            itemId, AnalysisMode.Commercial, TickConversions.FromSeconds(10), TickConversions.FromSeconds(20));

        // Commercials are excluded from the mode-wide fallback even when the item holds
        // exactly one: a non-matching range means a different commercial, not drift.
        _h.Store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [SegmentChangeHarness.MirroredDto(itemId, jellyfinRowId, MediaSegmentType.Commercial, Ticks(30), Ticks(40))],
        };
        var controller = CreateController();

        var result = await controller.DeleteSegmentAsync(jellyfinRowId, itemId, "commercial", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        var survivor = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(commercial.Id, survivor.Id);
        Assert.Equal(SegmentState.Active, survivor.State);
        Assert.Equal([(itemId, jellyfinRowId)], _h.Store.DeletedSegments);
    }

    [Fact]
    public async Task DeleteSegment_EmptySegmentId_Returns404_LikeTheOldLookupMiss()
    {
        using var scope = CreateScope();
        var database = _h.Database;
        var controller = CreateController();

        // The pre-cutover dispatch fell through both lookups for an empty id and
        // answered 404, which idempotent cleanup clients treat as already-gone.
        var result = await controller.DeleteSegmentAsync(Guid.Empty, Guid.NewGuid(), "intro", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CreateSegment_BadRequest_ForUnmappedSegmentType_WithoutWriting()
    {
        var itemId = Guid.NewGuid();
        using var scope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = _h.Database;
        var store = _h.Store;
        var controller = CreateController();

        // Type is omitted, so it binds as MediaSegmentType.Unknown, a defined enum
        // value with no mode mapping; the endpoint must 400 instead of crashing the
        // mapping into a 500.
        var response = await controller.CreateSegmentAsync(
            itemId,
            "providerId",
            new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = itemId, StartTicks = 10, EndTicks = 20 },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Empty(_h.Store.ReplacedItems);
    }

    [Fact]
    public async Task CreateSegment_PersistsUserRow_AndMirrorsIt()
    {
        var itemId = Guid.NewGuid();
        using var scope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = _h.Database;
        var store = _h.Store;
        var controller = CreateController();

        var response = await controller.CreateSegmentAsync(
            itemId,
            "providerId",
            SegmentChangeHarness.MirroredDto(itemId, Guid.NewGuid(), MediaSegmentType.Intro, Ticks(10), Ticks(20)),
            CancellationToken.None);

        Assert.IsType<OkResult>(response.Result);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(AnalysisMode.Introduction, row.Type);
        Assert.Equal(SegmentSource.User, row.Source);
        Assert.Equal(TickConversions.FromSeconds(10), row.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(20), row.EndTicks);
        var (replacedItemId, pushed) = Assert.Single(_h.Store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Equal(row.Id, Assert.Single(pushed).Id);
    }

    // Legacy wire contract: clients edit by re-POSTing a new range, so a non-commercial
    // POST replaces the mode's stored row instead of stacking a second one, while
    // commercials are inherently many per item and accumulate.
    [Theory]
    [InlineData(MediaSegmentType.Intro, 1)]
    [InlineData(MediaSegmentType.Commercial, 2)]
    public async Task CreateSegment_ReplacesNonCommercial_AndAppendsCommercial(MediaSegmentType type, int expectedRows)
    {
        var itemId = Guid.NewGuid();
        using var scope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = _h.Database;
        var controller = CreateController();

        foreach (var (start, end) in new[] { (10, 20), (300, 320) })
        {
            var response = await controller.CreateSegmentAsync(
                itemId,
                "providerId",
                SegmentChangeHarness.MirroredDto(itemId, Guid.NewGuid(), type, Ticks(start), Ticks(end)),
                CancellationToken.None);
            Assert.IsType<OkResult>(response.Result);
        }

        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(expectedRows, rows.Count);
        Assert.Contains(rows, row => row.StartTicks == TickConversions.FromSeconds(300) && row.EndTicks == TickConversions.FromSeconds(320));
    }

    private static EntrypointTestHelpers.PluginInstanceScope CreateScope()
        => new(EntrypointTestHelpers.CreateTempCacheDir());

    private SegmentEditorController CreateController() => new(_h.Change);
}
