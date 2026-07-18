// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for <see cref="SegmentEditorController.DeleteSegmentAsync"/> rollback behavior:
/// the plugin DB row is deleted before the Jellyfin-side delete, so a Jellyfin failure must
/// restore the row — including when the Jellyfin segment was already gone and the row was
/// identified from the plugin database alone.
/// </summary>
public sealed class TestSegmentEditorController
{
    [Fact]
    public async Task DeleteSegment_RestoresPluginRow_WhenJellyfinDeleteFails_AndJellyfinSegmentAlreadyGone()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction, isUserProvided: true, configHash: "cfg-1");

        // Jellyfin has no segment with this id (lookup returns null) and its delete throws.
        var manager = new FakeMediaSegmentManager { DeleteException = new InvalidOperationException("jellyfin down") };
        var controller = CreateController(manager, database);

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

        var movie = CreateMovie(itemId);
        var manager = new FakeMediaSegmentManager
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
            DeleteException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(manager, database, movie);

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

        var movie = CreateMovie(itemId);
        var manager = new FakeMediaSegmentManager
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
            DeleteException = new InvalidOperationException("jellyfin down"),
        };
        var controller = CreateController(manager, database, movie);

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
        var manager = new FakeMediaSegmentManager();
        var controller = CreateController(manager, database);
        var segmentId = Guid.NewGuid();

        await controller.DeleteSegmentAsync(segmentId, itemId, "intro", CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(itemId));
        Assert.Equal([segmentId], manager.DeletedSegmentIds);
    }

    private static SegmentEditorController CreateController(
        FakeMediaSegmentManager manager,
        IntroSkipper.Db.IIntroSkipperDatabase database,
        BaseItem? item = null)
    {
        var libraryManager = item is null
            ? EntrypointTestHelpers.CreateLibraryManager()
            : EntrypointTestHelpers.CreateLibraryManager(item);
        var service = new MediaSegmentEditorService(manager, libraryManager, NullLogger<MediaSegmentEditorService>.Instance);
        return new SegmentEditorController(service, database);
    }

    private static Movie CreateMovie(Guid id)
    {
        var item = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(item, "Id", id);
        EntrypointTestHelpers.EnsureNonVirtual(item);
        return item;
    }

    private sealed class FakeMediaSegmentManager : IMediaSegmentManager
    {
        public IReadOnlyList<MediaSegmentDto> ExistingSegments { get; init; } = [];

        public Exception? DeleteException { get; init; }

        public List<Guid> DeletedSegmentIds { get; } = [];

        public IEnumerable<(string Name, string Id)> GetSupportedProviders(BaseItem item) => [(Plugin.ProviderName, "intro-skipper")];

        public Task<IEnumerable<MediaSegmentDto>> GetSegmentsAsync(BaseItem item, IEnumerable<Jellyfin.Database.Implementations.Enums.MediaSegmentType>? typeFilter, LibraryOptions libraryOptions, bool filterByProvider = true)
            => Task.FromResult<IEnumerable<MediaSegmentDto>>(ExistingSegments);

        public Task<MediaSegmentDto> CreateSegmentAsync(MediaSegmentDto mediaSegment, string segmentProviderId) => Task.FromResult(mediaSegment);

        public Task DeleteSegmentAsync(Guid segmentId)
        {
            if (DeleteException is not null)
            {
                throw DeleteException;
            }

            DeletedSegmentIds.Add(segmentId);
            return Task.CompletedTask;
        }

        public Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunSegmentPluginProviders(BaseItem baseItem, LibraryOptions libraryOptions, bool forceOverwrite, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool HasSegments(Guid itemId) => false;

        public bool IsTypeSupported(BaseItem baseItem) => true;
    }
}
