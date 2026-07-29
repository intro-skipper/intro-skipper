// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for <see cref="SegmentEditorController.DeleteSegmentAsync"/> validation and rollback behavior:
/// the plugin DB row is deleted before the Jellyfin-side delete (tombstoning automatic rows,
/// hard-deleting user rows), so a Jellyfin failure must restore the exact row — via the
/// shared-id fast path or the exact-ticks fallback for uncorrelated Jellyfin ids.
/// </summary>
public sealed class SegmentEditorControllerTests
{
    [Fact]
    public async Task DeleteSegment_RestoresPluginRow_WhenJellyfinDeleteFails_AndJellyfinSegmentAlreadyGone()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var original = await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(100), TickConversions.FromSeconds(160));

        // Jellyfin has no segment with this id, and its delete throws: the already
        // hard-deleted user row must be re-inserted verbatim by the rollback.
        var store = new FakeJellyfinSegmentStore { DeleteSegmentException = new InvalidOperationException("jellyfin down") };
        var controller = CreateController(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteSegmentAsync(original.Id, itemId, "intro", CancellationToken.None));

        var rows = await database.GetSegmentsAsync(itemId);
        var restored = Assert.Single(rows);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(AnalysisMode.Introduction, restored.Type);
        Assert.Equal(TickConversions.FromSeconds(100), restored.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(160), restored.EndTicks);
        Assert.Equal(SegmentSource.User, restored.Source);
        Assert.Equal(SegmentState.Active, restored.State);
        Assert.Equal(original.ConfigHash, restored.ConfigHash);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
    }

    [Fact]
    public async Task DeleteSegment_RestoresPluginRow_WhenJellyfinDeleteFails_WithKnownJellyfinSegment()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var jellyfinSegmentId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(100, 160))],
            SegmentSource.Chapter,
            "cfg-2");
        var original = Assert.Single(await database.GetSegmentsAsync(itemId));

        // The Jellyfin row's id matches no plugin row (it predates the shared-id scheme),
        // so the plugin counterpart is matched by exact ticks. The automatic row is
        // tombstoned, then the failing Jellyfin delete flips it back to Active.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = jellyfinSegmentId,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                    StartTicks = TickConversions.FromSeconds(100),
                    EndTicks = TickConversions.FromSeconds(160),
                }
            ],
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteSegmentAsync(jellyfinSegmentId, itemId, "intro", CancellationToken.None));

        var restored = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.StartTicks, restored.StartTicks);
        Assert.Equal(original.EndTicks, restored.EndTicks);
        Assert.Equal(SegmentSource.Chapter, restored.Source);
        Assert.Equal(SegmentState.Active, restored.State);
        Assert.Equal("cfg-2", restored.ConfigHash);
        Assert.Equal(original.CreatedAt, restored.CreatedAt);
    }

    [Theory]
    [InlineData(Jellyfin.Database.Implementations.Enums.MediaSegmentType.Outro)]
    [InlineData((Jellyfin.Database.Implementations.Enums.MediaSegmentType)int.MaxValue)]
    public async Task DeleteSegment_RejectsMismatchedOrUnsupportedExistingSegmentType_WithoutMutatingEitherStore(
        Jellyfin.Database.Implementations.Enums.MediaSegmentType existingType)
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(100, 160))], SegmentSource.Chapter, "cfg-intro");
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(1200, 1260))], SegmentSource.Chapter, "cfg-credits");
        await database.SetEpisodeIdsAsync(itemId, AnalysisMode.Introduction, [itemId], "cfg-intro");
        await database.SetEpisodeIdsAsync(itemId, AnalysisMode.Credits, [itemId], "cfg-credits");

        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = segmentId,
                    ItemId = itemId,
                    Type = existingType,
                    StartTicks = TickConversions.FromSeconds(1200),
                    EndTicks = TickConversions.FromSeconds(1260),
                }
            ],
        };
        var controller = CreateController(store, database);

        var response = await controller.DeleteSegmentAsync(segmentId, itemId, "intro", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response);
        Assert.Empty(store.DeletedSegments);

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
        Assert.Contains(itemId, snapshot.EpisodeIdsByMode[AnalysisMode.Introduction]);
        Assert.Contains(itemId, snapshot.EpisodeIdsByMode[AnalysisMode.Credits]);
    }

    [Fact]
    public async Task DeleteSegment_RejectsTypeMismatch_OnCorrelatedPluginRow_WithoutMutatingEitherStore()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(1200, 1260))], SegmentSource.Chapter, "cfg-credits");
        var creditsRow = Assert.Single(await database.GetSegmentsAsync(itemId));

        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        // The shared id resolves to a Credits row, but "intro" was requested: reject
        // without touching either store.
        var response = await controller.DeleteSegmentAsync(creditsRow.Id, itemId, "intro", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response);
        Assert.Empty(store.DeletedSegments);
        var survivor = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(creditsRow.Id, survivor.Id);
        Assert.Equal(SegmentState.Active, survivor.State);
    }

    [Fact]
    public async Task DeleteSegment_RemovesPluginRow_WhenJellyfinDeleteSucceeds_AndJellyfinSegmentAlreadyGone()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var row = await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(100), TickConversions.FromSeconds(160));

        // Jellyfin segment already gone; the delete of the unknown shared id succeeds as
        // a no-op, so the orphaned user row is cleaned up.
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);
    }

    [Fact]
    public async Task DeleteSegment_TombstonesAutomaticSegment_AndSecondDeleteReturnsNotFound()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(100, 160))], SegmentSource.Chapter, "cfg-auto");
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);

        // The automatic row survives as a tombstone so re-analysis cannot re-add it.
        Assert.IsType<OkResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(row.Id, tombstone.Id);
        Assert.Equal(SegmentState.Suppressed, tombstone.State);
        Assert.Equal(SegmentSource.Chapter, tombstone.Source);
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);

        // Deleting the already-suppressed row again reports not-found.
        var second = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);
        Assert.IsType<NotFoundResult>(second);
    }

    [Fact]
    public async Task DeleteSegment_HardDeletesUserSegment_LeavingNoTombstone()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var row = await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Commercial, TickConversions.FromSeconds(10), TickConversions.FromSeconds(20));

        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = row.Id,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Commercial,
                    StartTicks = row.StartTicks,
                    EndTicks = row.EndTicks,
                }
            ],
        };
        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(row.Id, itemId, "commercial", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);
    }

    [Fact]
    public async Task DeleteSegment_WithMismatchedItemId_NeverTouchesOtherItemsJellyfinRow()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        using var jellyfinDb = new TempJellyfinDb();
        var store = new JellyfinSegmentStore(jellyfinDb.Factory, NullLogger<JellyfinSegmentStore>.Instance);
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();

        // Item A's plugin row and Jellyfin row share their id under the shared-guid scheme.
        var rowA = await database.AddUserSegmentAsync(
            itemA, AnalysisMode.Introduction, TickConversions.FromSeconds(10), TickConversions.FromSeconds(60));
        var rowB = await database.AddUserSegmentAsync(
            itemB, AnalysisMode.Introduction, TickConversions.FromSeconds(100), TickConversions.FromSeconds(160));

        var context = jellyfinDb.Factory.CreateDbContext();
        await using (context)
        {
            context.MediaSegments.Add(new MediaSegment
            {
                Id = rowA.Id,
                ItemId = itemA,
                Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                StartTicks = TickConversions.FromSeconds(10),
                EndTicks = TickConversions.FromSeconds(60),
                SegmentProviderId = JellyfinSegmentStore.ProviderId,
            });
            await context.SaveChangesAsync();
        }

        var controller = new SegmentEditorController(
            new MediaSegmentEditorService(store, new MediaSegmentMirror(store, new SegmentDtoFactory(database)), database), database);

        // Item B's id paired with item A's segment id: the item mismatch skips the shared-id
        // fast path, the Jellyfin lookup is scoped to item B and finds nothing, and neither
        // item's data is touched.
        var result = await controller.DeleteSegmentAsync(rowA.Id, itemB, "intro", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(rowA.Id, Assert.Single(await database.GetSegmentsAsync(itemA)).Id);
        Assert.Equal(rowB.Id, Assert.Single(await database.GetSegmentsAsync(itemB)).Id);

        var verify = jellyfinDb.Factory.CreateDbContext();
        await using (verify)
        {
            var survivor = Assert.Single(await verify.MediaSegments.AsNoTracking().ToListAsync());
            Assert.Equal(itemA, survivor.ItemId);
            Assert.Equal(rowA.Id, survivor.Id);
        }
    }

    private static SegmentEditorController CreateController(
        FakeJellyfinSegmentStore store,
        IntroSkipper.Db.IIntroSkipperDatabase database)
        => new(new MediaSegmentEditorService(store, new MediaSegmentMirror(store, new SegmentDtoFactory(database)), database), database);
}
