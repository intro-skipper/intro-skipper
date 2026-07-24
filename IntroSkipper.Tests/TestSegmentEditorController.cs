// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for <see cref="SegmentEditorController.DeleteSegmentAsync"/> validation and rollback behavior:
/// the plugin DB row is deleted before the Jellyfin-side delete, so a Jellyfin failure must
/// restore the row — including when the Jellyfin segment was already gone and the row was
/// identified from the plugin database alone.
/// </summary>
public sealed class SegmentEditorControllerTests
{
    [Fact]
    public async Task DeleteSegment_RestoresPluginRow_WhenJellyfinDeleteFails_AndJellyfinSegmentAlreadyGone()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction, isUserProvided: true, configHash: "cfg-1");

        // Jellyfin has no segment with this id (lookup returns null) and its delete throws.
        var store = new FakeJellyfinSegmentStore { DeleteSegmentException = new InvalidOperationException("jellyfin down") };
        var controller = CreateController(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteSegmentAsync(Guid.NewGuid(), itemId, "intro", CancellationToken.None));

        var rows = await database.GetSegmentsAsync(itemId);
        var restored = Assert.Single(rows);
        Assert.Equal(AnalysisMode.Introduction, restored.Type);
        Assert.Equal(100, restored.Start);
        Assert.Equal(160, restored.End);
        Assert.True(restored.IsUserProvided);
        Assert.Equal("cfg-1", restored.ConfigHash);
    }

    [Fact]
    public async Task DeleteSegment_RestoresPluginRow_WhenJellyfinDeleteFails_WithKnownJellyfinSegment()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction, isUserProvided: true, configHash: "cfg-2");

        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = segmentId,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                    StartTicks = TimeSpan.FromSeconds(100).Ticks,
                    EndTicks = TimeSpan.FromSeconds(160).Ticks,
                }
            ],
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteSegmentAsync(segmentId, itemId, "intro", CancellationToken.None));

        var rows = await database.GetSegmentsAsync(itemId);
        var restored = Assert.Single(rows);
        Assert.Equal(100, restored.Start);
        Assert.Equal(160, restored.End);
        Assert.True(restored.IsUserProvided);
        Assert.Equal("cfg-2", restored.ConfigHash);
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
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction, configHash: "cfg-intro");
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(1200, 1260)), AnalysisMode.Credits, configHash: "cfg-credits");
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
                    StartTicks = TimeSpan.FromSeconds(1200).Ticks,
                    EndTicks = TimeSpan.FromSeconds(1260).Ticks,
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
        Assert.Equal(100, intro.Start);
        Assert.Equal(160, intro.End);
        Assert.Equal("cfg-intro", intro.ConfigHash);
        var credits = Assert.Single(rows, row => row.Type == AnalysisMode.Credits);
        Assert.Equal(1200, credits.Start);
        Assert.Equal(1260, credits.End);
        Assert.Equal("cfg-credits", credits.ConfigHash);

        var snapshot = await database.GetSeasonQueueSnapshotAsync(itemId, [itemId]);
        Assert.Contains(itemId, snapshot.EpisodeIdsByMode[AnalysisMode.Introduction]);
        Assert.Contains(itemId, snapshot.EpisodeIdsByMode[AnalysisMode.Credits]);
    }

    [Fact]
    public async Task DeleteSegment_RestoresNonCommercialMetadata_WhenJellyfinRangeDriftedOutsideEpsilon()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction, configHash: "cfg-original");

        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = segmentId,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                    StartTicks = TimeSpan.FromSeconds(100.005).Ticks,
                    EndTicks = TimeSpan.FromSeconds(160.005).Ticks,
                }
            ],
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteSegmentAsync(segmentId, itemId, "intro", CancellationToken.None));

        var restored = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(100, restored.Start);
        Assert.Equal(160, restored.End);
        Assert.False(restored.IsUserProvided);
        Assert.Equal("cfg-original", restored.ConfigHash);
    }

    [Fact]
    public async Task DeleteSegment_RollbackRestoresExactRow_WhenMultipleCloseCommercialsExist()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // Two commercials 0.005s apart: farther than the facade's delete epsilon (0.001)
        // but closer than the 0.01 tolerance the controller used to re-match with. A
        // rollback must restore the actually-deleted row and its metadata, not the
        // neighbor's.
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Commercial, isUserProvided: false, configHash: "cfg-a");
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10.005, 20.005)), AnalysisMode.Commercial, isUserProvided: true, configHash: "cfg-b");

        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = segmentId,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Commercial,
                    StartTicks = TimeSpan.FromSeconds(10.005).Ticks,
                    EndTicks = TimeSpan.FromSeconds(20.005).Ticks,
                }
            ],
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteSegmentAsync(segmentId, itemId, "commercial", CancellationToken.None));

        var rows = (await database.GetSegmentsAsync(itemId)).OrderBy(s => s.Start).ToList();
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsUserProvided);
        Assert.Equal("cfg-a", rows[0].ConfigHash);
        var restored = rows[1];
        Assert.Equal(10.005, restored.Start);
        Assert.Equal(20.005, restored.End);
        Assert.True(restored.IsUserProvided);
        Assert.Equal("cfg-b", restored.ConfigHash);
    }

    [Fact]
    public async Task DeleteSegment_RemovesPluginRow_WhenJellyfinDeleteSucceeds_AndJellyfinSegmentAlreadyGone()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction);

        // Jellyfin segment already gone; the delete of the unknown id succeeds as a no-op,
        // so the orphaned plugin row is cleaned up.
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);
        var segmentId = Guid.NewGuid();

        await controller.DeleteSegmentAsync(segmentId, itemId, "intro", CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(itemId));
        Assert.Equal([(itemId, segmentId)], store.DeletedSegments);
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
        var segmentIdA = Guid.NewGuid();

        var context = jellyfinDb.Factory.CreateDbContext();
        await using (context)
        {
            context.MediaSegments.Add(new MediaSegment
            {
                Id = segmentIdA,
                ItemId = itemA,
                Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                StartTicks = TimeSpan.FromSeconds(10).Ticks,
                EndTicks = TimeSpan.FromSeconds(60).Ticks,
                SegmentProviderId = JellyfinSegmentStore.ProviderId,
            });
            await context.SaveChangesAsync();
        }

        await database.UpdateTimestampAsync(new Segment(itemB, new TimeRange(100, 160)), AnalysisMode.Introduction, isUserProvided: true, configHash: "cfg-b");

        var controller = new SegmentEditorController(new MediaSegmentEditorService(store), database);

        // Item B's id paired with item A's segment id: the caller's own orphaned plugin row
        // is still cleaned up, but item A's Jellyfin segment must survive.
        var result = await controller.DeleteSegmentAsync(segmentIdA, itemB, "intro", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemB));

        var verify = jellyfinDb.Factory.CreateDbContext();
        await using (verify)
        {
            var survivor = Assert.Single(await verify.MediaSegments.AsNoTracking().ToListAsync());
            Assert.Equal(itemA, survivor.ItemId);
            Assert.Equal(segmentIdA, survivor.Id);
        }
    }

    private static SegmentEditorController CreateController(
        FakeJellyfinSegmentStore store,
        IntroSkipper.Db.IIntroSkipperDatabase database)
        => new(new MediaSegmentEditorService(store), database);
}
