// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for <see cref="SegmentEditorController.DeleteSegmentAsync"/> validation and rollback behavior:
/// the plugin DB row is deleted before the Jellyfin-side delete, so a Jellyfin failure must
/// restore the row, including when the Jellyfin segment was already gone and the row was
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
                    Type = MediaSegmentType.Intro,
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

    [Fact]
    public async Task DeleteSegment_RestoresDetectedCreditsRow_WhenJellyfinDeleteFails_AndAnIntroductionOverlapsIt()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // Credits first: writing a detected Credits row is refused once an overlapping
        // Introduction exists, which is exactly the guard the restore must not re-trigger.
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(2300, 2500)), AnalysisMode.Credits, configHash: "cfg-credits");
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(0, 2400)), AnalysisMode.Introduction, isUserProvided: true, configHash: "cfg-intro");

        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = segmentId,
                    ItemId = itemId,
                    Type = MediaSegmentType.Outro,
                    StartTicks = TimeSpan.FromSeconds(2300).Ticks,
                    EndTicks = TimeSpan.FromSeconds(2500).Ticks,
                }
            ],
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteSegmentAsync(segmentId, itemId, "outro", CancellationToken.None));

        var rows = await database.GetSegmentsAsync(itemId);
        var restored = Assert.Single(rows, row => row.Type == AnalysisMode.Credits);
        Assert.Equal(2300, restored.Start);
        Assert.Equal(2500, restored.End);
        Assert.False(restored.IsUserProvided);
        Assert.Equal("cfg-credits", restored.ConfigHash);
        Assert.Single(rows, row => row.Type == AnalysisMode.Introduction);
    }

    [Fact]
    public async Task DeleteSegment_CreatesNoPluginRow_WhenJellyfinDeleteFails_AndTheSegmentHadNoPluginCounterpart()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // Another provider's Jellyfin segment: the editor may delete it by id, but the
        // plugin database has nothing to roll back to.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = segmentId,
                    ItemId = itemId,
                    Type = MediaSegmentType.Intro,
                    StartTicks = TimeSpan.FromSeconds(100).Ticks,
                    EndTicks = TimeSpan.FromSeconds(160).Ticks,
                }
            ],
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(store, database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteSegmentAsync(segmentId, itemId, "intro", CancellationToken.None));

        Assert.Empty(await database.GetSegmentsAsync(itemId));
    }

    [Theory]
    [InlineData(MediaSegmentType.Outro)]
    [InlineData((MediaSegmentType)int.MaxValue)]
    public async Task DeleteSegment_RejectsMismatchedOrUnsupportedExistingSegmentType_WithoutMutatingEitherStore(
        MediaSegmentType existingType)
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
                    Type = MediaSegmentType.Intro,
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
                    Type = MediaSegmentType.Commercial,
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

        var controller = new SegmentEditorController(
            new MediaSegmentEditorService(store, database, []),
            database,
            DatabaseTestHelpers.CreateCacheDatabase(DatabaseTestHelpers.CreateTempCacheDbPath()));

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

    [Fact]
    public async Task PutSegments_ReplacesAtomically_AndReturnsRefreshedView()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        SetLibrary(CreateMovie(itemId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [CreateSnapshot(itemId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId)]
        };
        var controller = CreateController(store, database, [new FakeMediaSegmentProvider(Plugin.ProviderName)]);

        var response = await controller.ReplaceSegmentsAsync(
            itemId,
            [
                new MediaSegmentDto
                {
                    Type = MediaSegmentType.Intro,
                    StartTicks = TimeSpan.FromSeconds(10).Ticks,
                    EndTicks = TimeSpan.FromSeconds(20).Ticks,
                }
            ],
            CancellationToken.None);

        var (writeItemId, writeSegments, writeTypes) = Assert.Single(store.ReplacedEditableTypes);
        Assert.Equal(itemId, writeItemId);
        Assert.Single(writeSegments);
        Assert.Equal(5, writeTypes.Count);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.True(row.IsUserProvided);
        Assert.Equal(10, row.Start);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var view = Assert.IsAssignableFrom<IReadOnlyList<EditorSegmentDto>>(ok.Value);
        var entry = Assert.Single(view);
        Assert.True(entry.IsUserProvided);
        Assert.Equal(Plugin.ProviderName, entry.ProviderName);
    }

    [Fact]
    public async Task PutSegments_EmptyBody_ClearsRowsAndSeasonState()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        SetLibrary(CreateMovie(itemId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction);

        // A movie outside the queue resolves its season-state key to its own id.
        await database.SetEpisodeIdsAsync(itemId, AnalysisMode.Introduction, [itemId], "hash");
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        var response = await controller.ReplaceSegmentsAsync(itemId, [], CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var (writeItemId, writeSegments, _) = Assert.Single(store.ReplacedEditableTypes);
        Assert.Equal(itemId, writeItemId);
        Assert.Empty(writeSegments);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
        var snapshot = await database.GetSeasonQueueSnapshotAsync(itemId, [itemId]);
        Assert.DoesNotContain(itemId, snapshot.EpisodeIdsByMode[AnalysisMode.Introduction]);
    }

    [Theory]
    [InlineData(-1L, 20L)]
    [InlineData(10L, 10L)]
    [InlineData(20L, 10L)]
    public async Task PutSegments_Returns400_ForInvalidRange(long startTicks, long endTicks)
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        SetLibrary(CreateMovie(itemId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        var response = await controller.ReplaceSegmentsAsync(
            itemId,
            [new MediaSegmentDto { Type = MediaSegmentType.Intro, StartTicks = startTicks, EndTicks = endTicks }],
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
    }

    [Fact]
    public async Task PutSegments_Returns400_ForNullElement()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        SetLibrary(CreateMovie(itemId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        var response = await controller.ReplaceSegmentsAsync(itemId, [null!], CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
    }

    [Fact]
    public async Task PutSegments_Returns400_ForRepeatedSegmentId_ButNotForRepeatedEmptyIds()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        SetLibrary(CreateMovie(itemId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);
        var segmentId = Guid.NewGuid();

        var duplicate = await controller.ReplaceSegmentsAsync(
            itemId,
            [
                new MediaSegmentDto { Id = segmentId, Type = MediaSegmentType.Intro, StartTicks = 10, EndTicks = 20 },
                new MediaSegmentDto { Id = segmentId, Type = MediaSegmentType.Outro, StartTicks = 30, EndTicks = 40 },
            ],
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(duplicate.Result);
        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(await database.GetSegmentsAsync(itemId));

        // The empty guid is how a caller asks for a generated id, so repeats are expected.
        var generated = await controller.ReplaceSegmentsAsync(
            itemId,
            [
                new MediaSegmentDto { Type = MediaSegmentType.Intro, StartTicks = 10, EndTicks = 20 },
                new MediaSegmentDto { Type = MediaSegmentType.Outro, StartTicks = 30, EndTicks = 40 },
            ],
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(generated.Result);
    }

    [Fact]
    public async Task PutSegments_Returns400_ForDuplicateNonCommercialType_AndUnsupportedType()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        SetLibrary(CreateMovie(itemId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        var duplicate = await controller.ReplaceSegmentsAsync(
            itemId,
            [
                new MediaSegmentDto { Type = MediaSegmentType.Intro, StartTicks = 0, EndTicks = 10 },
                new MediaSegmentDto { Type = MediaSegmentType.Intro, StartTicks = 20, EndTicks = 30 },
            ],
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(duplicate.Result);

        var unsupported = await controller.ReplaceSegmentsAsync(
            itemId,
            [new MediaSegmentDto { Type = (MediaSegmentType)int.MaxValue, StartTicks = 0, EndTicks = 10 }],
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(unsupported.Result);

        Assert.Equal(0, store.WriteCallCount);
    }

    [Fact]
    public async Task PutSegments_Returns404_WhenItemUnknown()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        SetLibrary();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, DatabaseTestHelpers.CreateTempSegmentDatabase());

        var response = await controller.ReplaceSegmentsAsync(Guid.NewGuid(), [], CancellationToken.None);

        Assert.IsType<NotFoundResult>(response.Result);
        Assert.Equal(0, store.WriteCallCount);
    }

    [Fact]
    public async Task GetSegments_AnnotatesProviderAndUserFlag()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        SetLibrary(CreateMovie(itemId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction, isUserProvided: true);
        var otherProviderId = JellyfinSegmentStore.DeriveProviderId("Other Provider");
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments =
            [
                // Drifted range: the non-commercial user flag must still resolve by mode alone.
                CreateSnapshot(itemId, MediaSegmentType.Intro, 100.005, 160.005, JellyfinSegmentStore.ProviderId),
                CreateSnapshot(itemId, MediaSegmentType.Outro, 1200, 1260, otherProviderId),
            ]
        };
        var controller = CreateController(
            store,
            database,
            [new FakeMediaSegmentProvider(Plugin.ProviderName), new FakeMediaSegmentProvider("Other Provider")]);

        var response = await controller.GetSegmentsAsync(itemId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var view = Assert.IsAssignableFrom<IReadOnlyList<EditorSegmentDto>>(ok.Value);
        Assert.Equal(2, view.Count);
        var intro = Assert.Single(view, entry => entry.Type == MediaSegmentType.Intro);
        Assert.Equal(Plugin.ProviderName, intro.ProviderName);
        Assert.True(intro.IsUserProvided);
        var outro = Assert.Single(view, entry => entry.Type == MediaSegmentType.Outro);
        Assert.Equal("Other Provider", outro.ProviderName);
        Assert.Null(outro.IsUserProvided);
    }

    [Fact]
    public async Task GetSegments_Returns404_WhenItemUnknown()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        SetLibrary();
        var controller = CreateController(new FakeJellyfinSegmentStore(), DatabaseTestHelpers.CreateTempSegmentDatabase());

        var response = await controller.GetSegmentsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task Copy_AppliesToFoundTargets_AndReportsMissingOnes()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        SetLibrary(CreateMovie(sourceId), CreateMovie(targetId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [CreateSnapshot(sourceId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId)]
        };
        var controller = CreateController(store, database);

        var response = await controller.CopySegmentsAsync(
            new CopySegmentsRequest(
                sourceId,
                [targetId, missingId],
                [MediaSegmentType.Intro],
                TimeSpan.FromSeconds(5).Ticks),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<CopySegmentsResponse>(ok.Value);
        Assert.Equal(2, result.Results.Count);
        var success = Assert.Single(result.Results, entry => entry.ItemId == targetId);
        Assert.True(success.Success);
        var failure = Assert.Single(result.Results, entry => entry.ItemId == missingId);
        Assert.False(failure.Success);
        Assert.Equal("Item not found.", failure.Error);

        var (writeItemId, writeSegments, writeTypes) = Assert.Single(store.ReplacedEditableTypes);
        Assert.Equal(targetId, writeItemId);
        Assert.Equal([MediaSegmentType.Intro], writeTypes);
        var copied = Assert.Single(writeSegments);
        Assert.Equal(TimeSpan.FromSeconds(15).Ticks, copied.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(25).Ticks, copied.EndTicks);
        var targetRow = Assert.Single(await database.GetSegmentsAsync(targetId));
        Assert.True(targetRow.IsUserProvided);
        Assert.Equal(15, targetRow.Start);
        Assert.Equal(25, targetRow.End);
    }

    [Fact]
    public async Task Copy_DefaultTypes_ScopesToSourcePresentTypes_AndPreservesTargetOthers()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        SetLibrary(CreateMovie(sourceId), CreateMovie(targetId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // The target has a user-corrected Credits segment the copy must not touch.
        await database.UpdateTimestampAsync(new Segment(targetId, new TimeRange(1200, 1260)), AnalysisMode.Credits, isUserProvided: true);
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [CreateSnapshot(sourceId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId)]
        };
        var controller = CreateController(store, database);

        var response = await controller.CopySegmentsAsync(new CopySegmentsRequest(sourceId, [targetId]), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var (writeItemId, writeSegments, writeTypes) = Assert.Single(store.ReplacedEditableTypes);
        Assert.Equal(targetId, writeItemId);
        // Only the source-present type is replaced, never all managed types.
        Assert.Equal([MediaSegmentType.Intro], writeTypes);
        Assert.Single(writeSegments);
        var targetRows = await database.GetSegmentsAsync(targetId);
        var credits = Assert.Single(targetRows, row => row.Type == AnalysisMode.Credits);
        Assert.True(credits.IsUserProvided);
        Assert.Single(targetRows, row => row.Type == AnalysisMode.Introduction);
    }

    [Fact]
    public async Task Copy_RequestedTypeMissingFromSource_LeavesThatTypeUntouchedOnTargets()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        SetLibrary(CreateMovie(sourceId), CreateMovie(targetId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // The target's user-corrected Credits must survive: the source has no Outro to copy.
        await database.UpdateTimestampAsync(new Segment(targetId, new TimeRange(1200, 1260)), AnalysisMode.Credits, isUserProvided: true);
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [CreateSnapshot(sourceId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId)]
        };
        var controller = CreateController(store, database);

        var response = await controller.CopySegmentsAsync(
            new CopySegmentsRequest(sourceId, [targetId], [MediaSegmentType.Intro, MediaSegmentType.Outro]),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var (_, _, writeTypes) = Assert.Single(store.ReplacedEditableTypes);
        Assert.Equal([MediaSegmentType.Intro], writeTypes);
        var credits = Assert.Single(await database.GetSegmentsAsync(targetId), row => row.Type == AnalysisMode.Credits);
        Assert.True(credits.IsUserProvided);
        Assert.Equal(1200, credits.Start);
    }

    [Fact]
    public async Task Copy_MultiProviderSourceType_CopiesOwnRowOnce()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        SetLibrary(CreateMovie(sourceId), CreateMovie(targetId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // Jellyfin permits several providers to hold an Intro for one item; the plugin's
        // unique index permits only one, so exactly one must be copied.
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments =
            [
                CreateSnapshot(sourceId, MediaSegmentType.Intro, 0, 5, JellyfinSegmentStore.DeriveProviderId("Other Provider")),
                CreateSnapshot(sourceId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId),
            ]
        };
        var controller = CreateController(store, database);

        var response = await controller.CopySegmentsAsync(
            new CopySegmentsRequest(sourceId, [targetId], [MediaSegmentType.Intro]),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<CopySegmentsResponse>(ok.Value);
        Assert.True(Assert.Single(result.Results).Success);

        var (_, writeSegments, _) = Assert.Single(store.ReplacedEditableTypes);
        var copied = Assert.Single(writeSegments);

        // Intro Skipper's own row wins over the foreign provider's.
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, copied.StartTicks);
        var row = Assert.Single(await database.GetSegmentsAsync(targetId));
        Assert.Equal(10, row.Start);
    }

    [Fact]
    public async Task Copy_CommercialsCollapsedByClamp_AreDeduplicated()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        SetLibrary(CreateMovie(sourceId), CreateMovie(targetId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments =
            [
                CreateSnapshot(sourceId, MediaSegmentType.Commercial, 5, 30, JellyfinSegmentStore.ProviderId),
                CreateSnapshot(sourceId, MediaSegmentType.Commercial, 8, 30, JellyfinSegmentStore.ProviderId),
            ]
        };
        var controller = CreateController(store, database);

        // Shifting -10s clamps both starts to 0, collapsing them onto one range.
        var response = await controller.CopySegmentsAsync(
            new CopySegmentsRequest(sourceId, [targetId], null, -TimeSpan.FromSeconds(10).Ticks),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.True(Assert.Single(Assert.IsType<CopySegmentsResponse>(ok.Value).Results).Success);
        var (_, writeSegments, _) = Assert.Single(store.ReplacedEditableTypes);
        var copied = Assert.Single(writeSegments);
        Assert.Equal(0, copied.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(20).Ticks, copied.EndTicks);
        Assert.Single(await database.GetSegmentsAsync(targetId));
    }

    [Fact]
    public async Task Copy_ClampsShiftedStartToZero_AndRejectsEliminatedSegments()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        SetLibrary(CreateMovie(sourceId), CreateMovie(targetId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [CreateSnapshot(sourceId, MediaSegmentType.Intro, 10, 20, JellyfinSegmentStore.ProviderId)]
        };
        var controller = CreateController(store, database);

        var clamped = await controller.CopySegmentsAsync(
            new CopySegmentsRequest(sourceId, [targetId], null, -TimeSpan.FromSeconds(15).Ticks),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(clamped.Result);
        var (_, writeSegments, _) = Assert.Single(store.ReplacedEditableTypes);
        var copied = Assert.Single(writeSegments);
        Assert.Equal(0, copied.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, copied.EndTicks);

        var eliminated = await controller.CopySegmentsAsync(
            new CopySegmentsRequest(sourceId, [targetId], null, -TimeSpan.FromSeconds(25).Ticks),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(eliminated.Result);
    }

    [Fact]
    public async Task Copy_Returns400_ForZeroDurationSourceSegment()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        SetLibrary(CreateMovie(sourceId), CreateMovie(targetId));
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore
        {
            ItemSegments = [CreateSnapshot(sourceId, MediaSegmentType.Intro, 10, 10, JellyfinSegmentStore.ProviderId)]
        };
        var controller = CreateController(store, database);

        var response = await controller.CopySegmentsAsync(
            new CopySegmentsRequest(sourceId, [targetId]),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(await database.GetSegmentsAsync(targetId));
    }

    [Fact]
    public async Task Copy_Returns400_ForEmptyTargetsTypesOrSourceSelection_And404ForUnknownSource()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var sourceId = Guid.NewGuid();
        SetLibrary(CreateMovie(sourceId));
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, DatabaseTestHelpers.CreateTempSegmentDatabase());

        var unknownSource = await controller.CopySegmentsAsync(new CopySegmentsRequest(Guid.NewGuid(), [Guid.NewGuid()]), CancellationToken.None);
        Assert.IsType<NotFoundResult>(unknownSource.Result);

        var emptyTargets = await controller.CopySegmentsAsync(new CopySegmentsRequest(sourceId, []), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(emptyTargets.Result);

        var emptyTypes = await controller.CopySegmentsAsync(new CopySegmentsRequest(sourceId, [Guid.NewGuid()], []), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(emptyTypes.Result);

        // The source has no segments at all, so any type selection resolves to nothing.
        var emptySelection = await controller.CopySegmentsAsync(new CopySegmentsRequest(sourceId, [Guid.NewGuid()]), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(emptySelection.Result);

        Assert.Equal(0, store.WriteCallCount);
    }

    [Fact]
    public async Task Orphans_ListsOnlyMissingItems_AndDeleteCleansOwnRows()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var liveId = Guid.NewGuid();
        var orphanId = Guid.NewGuid();
        var foreignOnlyOrphanId = Guid.NewGuid();
        SetLibrary(CreateMovie(liveId));
        var store = new FakeJellyfinSegmentStore
        {
            SegmentCounts =
            [
                new ItemSegmentCounts(liveId, 1, 0),
                new ItemSegmentCounts(orphanId, 2, 1),
                new ItemSegmentCounts(Guid.Empty, 1, 0),
                new ItemSegmentCounts(foreignOnlyOrphanId, 0, 3),
            ]
        };
        var controller = CreateController(store, DatabaseTestHelpers.CreateTempSegmentDatabase());

        var listResponse = await controller.GetOrphanedSegmentsAsync(CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResponse.Result);
        var orphans = Assert.IsAssignableFrom<IReadOnlyList<OrphanedItemSegments>>(listOk.Value);
        Assert.Equal(3, orphans.Count);
        Assert.DoesNotContain(orphans, entry => entry.ItemId == liveId);
        var orphanEntry = Assert.Single(orphans, entry => entry.ItemId == orphanId);
        Assert.Equal(2, orphanEntry.OwnCount);
        Assert.Equal(1, orphanEntry.OtherCount);

        var deleteResponse = await controller.DeleteOrphanedSegmentsAsync(CancellationToken.None);
        var deleteOk = Assert.IsType<OkObjectResult>(deleteResponse.Result);
        var deleted = Assert.IsType<DeleteOrphansResponse>(deleteOk.Value);
        Assert.Equal(1, deleted.DeletedItemCount);
        Assert.Equal([orphanId], store.DeletedOwnItemIds);
    }

    private static void SetLibrary(params MediaBrowser.Controller.Entities.BaseItem[] items)
    {
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager(items));
        EntrypointTestHelpers.SetPropertyOrField(
            Plugin.Instance!,
            "QueuedMediaItems",
            new ConcurrentDictionary<Guid, List<QueuedEpisode>>());
    }

    private static Movie CreateMovie(Guid id) => EntrypointTestHelpers.CreateMovie(id);

    private static JellyfinSegmentSnapshot CreateSnapshot(
        Guid itemId,
        MediaSegmentType type,
        double startSeconds,
        double endSeconds,
        string providerId)
        => new(
            Guid.NewGuid(),
            itemId,
            type,
            TimeSpan.FromSeconds(startSeconds).Ticks,
            TimeSpan.FromSeconds(endSeconds).Ticks,
            providerId);

    private static SegmentEditorController CreateController(
        FakeJellyfinSegmentStore store,
        IIntroSkipperDatabase database,
        IEnumerable<IMediaSegmentProvider>? providers = null)
        => new(
            new MediaSegmentEditorService(store, database, providers ?? []),
            database,
            DatabaseTestHelpers.CreateCacheDatabase(DatabaseTestHelpers.CreateTempCacheDbPath()));
}
