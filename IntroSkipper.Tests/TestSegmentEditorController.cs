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
/// Tests for <see cref="SegmentEditorController"/> over the durable segment-change
/// coordinator: the legacy delete dispatch (shared-id fast path, uncorrelated exact
/// fallback with one tick of tolerance and the non-commercial mode-wide fallback),
/// validation wire behavior, and accepted-plus-pending semantics when the Jellyfin
/// projection cannot apply synchronously.
/// </summary>
public sealed class SegmentEditorControllerTests
{
    [Fact]
    public async Task DeleteSegment_KeepsAuthoritativeDelete_WhenProjectionFails()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var original = await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(100), TickConversions.FromSeconds(160));

        // The row is mirrored under its shared id, but Jellyfin is down (both the
        // journaled targeted delete and the convergence write fail): the committed
        // delete must stand (202, not an error), with the journaled work owning the
        // mirror from there — no rollback resurrects a user delete.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = original.Id,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                    StartTicks = original.StartTicks,
                    EndTicks = original.EndTicks,
                }
            ],
            WriteException = new InvalidOperationException("jellyfin down"),
            DeleteSegmentException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(original.Id, itemId, "intro", CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value);
        Assert.Equal("Pending", body.Projection);
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
    }

    [Fact]
    public async Task DeleteSegment_KeepsTombstone_WhenUncorrelatedJellyfinDeleteFails()
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

        // The Jellyfin row's id matches no plugin row (it predates the shared-id
        // scheme), so the plugin counterpart is matched by exact ticks and
        // tombstoned. The failing Jellyfin delete no longer rolls that back: the
        // change reports accepted-plus-pending and the journaled foreign-row delete
        // retries until Jellyfin converges.
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

        var result = await controller.DeleteSegmentAsync(jellyfinSegmentId, itemId, "intro", CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<SegmentChangeAcceptedResponse>(accepted.Value);
        Assert.Equal("Pending", body.Projection);
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(original.Id, tombstone.Id);
        Assert.Equal(SegmentState.Suppressed, tombstone.State);
        Assert.Empty(store.DeletedSegments);
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
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [itemId], "cfg-intro");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [itemId], "cfg-credits");

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
        Assert.Equal("cfg-intro", snapshot.AnalyzedConfigHashes[(itemId, AnalysisMode.Introduction)]);
        Assert.Equal("cfg-credits", snapshot.AnalyzedConfigHashes[(itemId, AnalysisMode.Credits)]);
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

        // Jellyfin segment already gone; the correlated delete commits and the
        // convergence finds nothing left to change, so the orphaned user row is
        // cleaned up as a plain synchronous success.
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Empty(await store.GetOwnSegmentsAsync(itemId, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSegment_TombstonesAutomaticSegment_AndSecondDeleteSucceedsIdempotently()
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
        Assert.Empty(await store.GetOwnSegmentsAsync(itemId, CancellationToken.None));

        // Deleting the already-suppressed row again succeeds idempotently (the plugin
        // already treats it as deleted) instead of 404ing.
        var second = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);
        Assert.IsType<OkResult>(second);
    }

    [Fact]
    public async Task DeleteSegment_GhostJellyfinRow_ConvergesMirrorAndSucceeds()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(100, 160))], SegmentSource.Chapter, "cfg-auto");
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.NotNull(await database.DeleteSegmentAsync(itemId, row.Id));

        // Jellyfin re-added the row from a provider read predating the delete (the
        // mirror's documented race); the user deletes the visible ghost again.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = row.Id,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                    StartTicks = row.StartTicks,
                    EndTicks = row.EndTicks,
                }
            ],
        };
        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(row.Id, itemId, "intro", CancellationToken.None);

        // The delete converges the item's mirror instead of 404ing while the ghost
        // keeps serving; the tombstone stays so re-analysis cannot re-add the range.
        Assert.IsType<OkResult>(result);
        Assert.Empty(await store.GetOwnSegmentsAsync(itemId, CancellationToken.None));
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(row.Id, tombstone.Id);
        Assert.Equal(SegmentState.Suppressed, tombstone.State);
    }

    [Fact]
    public async Task DeleteSegment_DriftedUncorrelatedRow_MatchesSingleActiveRowOfMode()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        var itemId = Guid.NewGuid();
        var jellyfinRowId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(100, 160))], SegmentSource.Chapter, "cfg-auto");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [itemId], "cfg-auto");
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        // The Jellyfin row drifted from the plugin row (re-analysis while mirroring was
        // off) beyond the one-tick tolerance: the mode-wide fallback still finds the
        // item's single active intro, so the delete does not silently undo itself.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = jellyfinRowId,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                    StartTicks = TickConversions.FromSeconds(102),
                    EndTicks = TickConversions.FromSeconds(162),
                }
            ],
        };

        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(jellyfinRowId, itemId, "intro", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(row.Id, tombstone.Id);
        Assert.Equal(SegmentState.Suppressed, tombstone.State);
        Assert.Contains((itemId, jellyfinRowId), store.DeletedSegments);
        var snapshot = await database.GetSeasonQueueSnapshotAsync(itemId, [itemId]);
        Assert.False(snapshot.AnalyzedConfigHashes.ContainsKey((itemId, AnalysisMode.Introduction)));
    }

    [Fact]
    public async Task DeleteSegment_DriftedRow_KeepsExactMatching_WhenModeHasMultipleActiveRows()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        var itemId = Guid.NewGuid();
        var jellyfinRowId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(10), TickConversions.FromSeconds(60));
        await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(100), TickConversions.FromSeconds(160));

        // Two active intros make the mode-wide fallback ambiguous: only the named
        // Jellyfin row is removed, neither plugin row is guessed at.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = jellyfinRowId,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                    StartTicks = TickConversions.FromSeconds(200),
                    EndTicks = TickConversions.FromSeconds(260),
                }
            ],
        };
        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(jellyfinRowId, itemId, "intro", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Equal(2, (await database.GetSegmentsAsync(itemId)).Count);
        Assert.Equal([(itemId, jellyfinRowId)], store.DeletedSegments);
    }

    [Fact]
    public async Task DeleteSegment_DriftedCommercialRow_KeepsExactMatching()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        var itemId = Guid.NewGuid();
        var jellyfinRowId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var commercial = await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Commercial, TickConversions.FromSeconds(10), TickConversions.FromSeconds(20));

        // Commercials are excluded from the mode-wide fallback even when the item holds
        // exactly one: a non-matching range means a different commercial, not drift.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = jellyfinRowId,
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Commercial,
                    StartTicks = TickConversions.FromSeconds(30),
                    EndTicks = TickConversions.FromSeconds(40),
                }
            ],
        };
        var controller = CreateController(store, database);

        var result = await controller.DeleteSegmentAsync(jellyfinRowId, itemId, "commercial", CancellationToken.None);

        Assert.IsType<OkResult>(result);
        var survivor = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(commercial.Id, survivor.Id);
        Assert.Equal(SegmentState.Active, survivor.State);
        Assert.Equal([(itemId, jellyfinRowId)], store.DeletedSegments);
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
        Assert.Empty(await store.GetOwnSegmentsAsync(itemId, CancellationToken.None));
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

        var controller = CreateController(store, database);

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

    [Fact]
    public async Task DeleteSegment_EmptySegmentId_Returns404_LikeTheOldLookupMiss()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var controller = CreateController(new FakeJellyfinSegmentStore(), database);

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
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        // Type is omitted, so it binds as MediaSegmentType.Unknown — a defined enum
        // value with no mode mapping; the endpoint must 400 instead of crashing the
        // mapping into a 500.
        var response = await controller.CreateSegmentAsync(
            itemId,
            "providerId",
            new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = itemId, StartTicks = 10, EndTicks = 20 },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Empty(store.ReplacedItems);
    }

    [Fact]
    public async Task CreateSegment_PersistsUserRow_AndMirrorsIt()
    {
        var itemId = Guid.NewGuid();
        using var scope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        var response = await controller.CreateSegmentAsync(
            itemId,
            "providerId",
            new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                StartTicks = TickConversions.FromSeconds(10),
                EndTicks = TickConversions.FromSeconds(20),
            },
            CancellationToken.None);

        Assert.IsType<OkResult>(response.Result);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(AnalysisMode.Introduction, row.Type);
        Assert.Equal(SegmentSource.User, row.Source);
        Assert.Equal(TickConversions.FromSeconds(10), row.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(20), row.EndTicks);
        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Equal(row.Id, Assert.Single(pushed).Id);
    }

    [Fact]
    public async Task CreateSegment_NonCommercial_ReplacesStoredSegmentsOfMode()
    {
        var itemId = Guid.NewGuid();
        using var scope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        // Legacy wire contract: clients edit by re-POSTing a new range, so the second
        // POST must replace the first row instead of stacking a second intro.
        foreach (var (start, end) in new[] { (10, 20), (12, 25) })
        {
            var response = await controller.CreateSegmentAsync(
                itemId,
                "providerId",
                new MediaSegmentDto
                {
                    Id = Guid.NewGuid(),
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro,
                    StartTicks = TickConversions.FromSeconds(start),
                    EndTicks = TickConversions.FromSeconds(end),
                },
                CancellationToken.None);
            Assert.IsType<OkResult>(response.Result);
        }

        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(AnalysisMode.Introduction, row.Type);
        Assert.Equal(TickConversions.FromSeconds(12), row.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(25), row.EndTicks);
    }

    [Fact]
    public async Task CreateSegment_Commercial_AppendsWithoutReplacing()
    {
        var itemId = Guid.NewGuid();
        using var scope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        var controller = CreateController(store, database);

        // Commercials are inherently many per item: distinct ranges accumulate.
        foreach (var (start, end) in new[] { (10, 20), (300, 320) })
        {
            var response = await controller.CreateSegmentAsync(
                itemId,
                "providerId",
                new MediaSegmentDto
                {
                    Id = Guid.NewGuid(),
                    ItemId = itemId,
                    Type = Jellyfin.Database.Implementations.Enums.MediaSegmentType.Commercial,
                    StartTicks = TickConversions.FromSeconds(start),
                    EndTicks = TickConversions.FromSeconds(end),
                },
                CancellationToken.None);
            Assert.IsType<OkResult>(response.Result);
        }

        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(AnalysisMode.Commercial, r.Type));
    }

    private static SegmentEditorController CreateController(
        IJellyfinSegmentStore store,
        IntroSkipper.Db.IntroSkipperDatabase database)
        => DatabaseTestHelpers.CreateSegmentEditorController(store, database);
}
