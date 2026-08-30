// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestMediaSegmentRefreshService
{
    [Fact]
    public async Task RefreshAsync_AwaitsSegmentStoreWrite_BeforeCompleting()
    {
        var itemId = Guid.NewGuid();
        using var scope = CreatePluginScope();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The plugin database is empty, so the mirrored row is what makes the sync a
        // write; an empty-vs-empty sync would skip the store entirely.
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(itemId)],
            WriteEntered = writeEntered,
            WriteGate = writeGate,
            BlockedItemId = itemId
        };
        var refresher = CreateRefresher(store, itemId);

        var refreshTask = refresher.RefreshAsync([itemId], CancellationToken.None);

        // Wait until the refresh has passed the DTO factory and is parked on the closed
        // gate inside the store write; only then does the task's incompleteness prove
        // the write is awaited rather than fired and forgotten.
        await writeEntered.Task;

        Assert.False(refreshTask.IsCompleted);

        writeGate.SetResult();
        await refreshTask;

        var (replacedItemId, _) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
    }

    [Fact]
    public async Task RefreshAsync_PushesFactoryOutput_ForSeededPluginDatabase()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(100, 160)), new Segment(itemId, new TimeRange(200, 230))],
            SegmentSource.Chapter);
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Credits,
            [new Segment(itemId, new TimeRange(1200, 1260)), new Segment(itemId, new TimeRange(1300, 1330))],
            SegmentSource.Chromaprint);

        // Tombstone one credits row: suppressed rows must never be pushed to Jellyfin.
        var seeded = await database.GetSegmentsAsync(itemId);
        var suppressedRow = seeded.First(row => row.Type == AnalysisMode.Credits && row.StartTicks == TickConversions.FromSeconds(1300));
        await database.DeleteSegmentAsync(itemId, suppressedRow.Id);

        using var scope = CreatePluginScope();
        var store = new FakeJellyfinSegmentStore();
        var refresher = CreateRefresher(store, itemId, new SegmentDtoFactory(database));

        await refresher.RefreshAsync([itemId], CancellationToken.None);

        var active = await database.GetSegmentsAsync(itemId);
        Assert.Equal(3, active.Count);

        // Every active row is pushed 1:1 with its plugin Guid as the DTO id.
        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Equal(active.Count, pushed.Count);
        foreach (var row in active)
        {
            var dto = Assert.Single(pushed, segment => segment.Id == row.Id);
            Assert.Equal(itemId, dto.ItemId);
            Assert.Equal(row.StartTicks, dto.StartTicks);
            Assert.Equal(row.EndTicks, dto.EndTicks);
        }

        Assert.Equal(2, pushed.Count(segment => segment.Type == MediaSegmentType.Intro));
        var outro = Assert.Single(pushed, segment => segment.Type == MediaSegmentType.Outro);
        Assert.Equal(TickConversions.FromSeconds(1200), outro.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(1260), outro.EndTicks);
        Assert.DoesNotContain(pushed, segment => segment.Id == suppressedRow.Id);
    }

    [Fact]
    public async Task RefreshAsync_DisabledItem_PushesOnlyUserProvidedSegments()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(100, 160))],
            SegmentSource.Chapter);
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Credits,
            [new Segment(itemId, new TimeRange(1200, 1260))],
            SegmentSource.Chromaprint);
        var userRow = await database.AddUserSegmentAsync(
            itemId,
            AnalysisMode.Introduction,
            TickConversions.FromSeconds(300),
            TickConversions.FromSeconds(330));

        // The disable flag is item-scoped on the sync path: an unrelated season key
        // still disables the item.
        await database.SetItemDisabledAsync(Guid.NewGuid(), itemId, disabled: true);

        using var scope = CreatePluginScope();
        var store = new FakeJellyfinSegmentStore();
        var refresher = CreateRefresher(store, itemId, new SegmentDtoFactory(database));

        await refresher.RefreshAsync([itemId], CancellationToken.None);

        // Stored segments are untouched; only the user row crosses to Jellyfin.
        Assert.Equal(3, (await database.GetSegmentsAsync(itemId)).Count);
        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        var dto = Assert.Single(pushed);
        Assert.Equal(userRow.Id, dto.Id);
        Assert.Equal(MediaSegmentType.Intro, dto.Type);
        Assert.Equal(TickConversions.FromSeconds(300), dto.StartTicks);
        Assert.Equal(TickConversions.FromSeconds(330), dto.EndTicks);
    }

    [Fact]
    public async Task RefreshAsync_DisabledItem_WithOnlyAutomaticSegments_PushesEmptyReplace()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(100, 160))],
            SegmentSource.Chapter);
        await database.SetItemDisabledAsync(Guid.NewGuid(), itemId, disabled: true);

        using var scope = CreatePluginScope();
        // A previously mirrored row: the disable-sync must push the empty replace
        // that deletes it (a mirror with no rows would just skip).
        var store = new FakeJellyfinSegmentStore { ExistingSegments = [CreateMirroredDto(itemId)] };
        var refresher = CreateRefresher(store, itemId, new SegmentDtoFactory(database));

        await refresher.RefreshAsync([itemId], CancellationToken.None);

        // The empty replace is what deletes the item's mirrored rows in production.
        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Empty(pushed);
    }

    [Fact]
    public async Task RefreshAsync_SkipsJellyfinWrite_WhenMirrorAlreadyMatches()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(100, 160))],
            SegmentSource.Chapter);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        using var scope = CreatePluginScope();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                new MediaSegmentDto
                {
                    Id = row.Id,
                    ItemId = itemId,
                    Type = MediaSegmentType.Intro,
                    StartTicks = row.StartTicks,
                    EndTicks = row.EndTicks
                }
            ]
        };
        var refresher = CreateRefresher(store, itemId, new SegmentDtoFactory(database));

        // The mirror already holds exactly what the factory would push: the bulk
        // refresh stays read-only instead of rewriting the unchanged rows.
        await refresher.RefreshAsync([itemId], CancellationToken.None);

        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(store.ReplacedItems);

        // Any plugin-side change breaks the match and the next refresh writes again.
        await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Credits, TickConversions.FromSeconds(1200), TickConversions.FromSeconds(1260));
        await refresher.RefreshAsync([itemId], CancellationToken.None);

        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Equal(2, pushed.Count);
    }

    [Fact]
    public async Task RefreshAsync_LogsAndReturnsAfterStoreFailure()
    {
        var itemId = Guid.NewGuid();
        using var scope = CreatePluginScope();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(itemId)],
            WriteException = new InvalidOperationException("boom")
        };
        var refresher = CreateRefresher(store, itemId);

        await refresher.RefreshAsync([itemId], CancellationToken.None);

        Assert.Equal(1, store.WriteCallCount);
        var (replacedItemId, _) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
    }

    [Fact]
    public async Task RefreshAsync_RethrowsCriticalException()
    {
        var itemId = Guid.NewGuid();
        using var scope = CreatePluginScope();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateMirroredDto(itemId)],
            WriteException = new ThreadInterruptedException()
        };
        var refresher = CreateRefresher(store, itemId);

        await Assert.ThrowsAsync<ThreadInterruptedException>(
            () => refresher.RefreshAsync([itemId], CancellationToken.None));

        Assert.Equal(1, store.WriteCallCount);
    }

    [Fact]
    public async Task RefreshAsync_ResolvesItemsViaLibraryManager_SkippingEmptyDuplicateAndUnknownIds()
    {
        var itemId = Guid.NewGuid();
        using var scope = CreatePluginScope();
        var store = new FakeJellyfinSegmentStore { ExistingSegments = [CreateMirroredDto(itemId)] };
        var refresher = CreateRefresher(store, itemId);

        await refresher.RefreshAsync([itemId, Guid.Empty, itemId, Guid.NewGuid()], CancellationToken.None);

        var (replacedItemId, _) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
    }

    [Fact]
    public async Task AllWrites_DoNothing_WhenUpdateMediaSegmentsDisabled()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(
            Plugin.Instance!,
            "Configuration",
            new PluginConfiguration { UpdateMediaSegments = false });
        var store = new FakeJellyfinSegmentStore();
        var refresher = CreateRefresher(store);

        // The mirror flag lives in the service, not at call sites: every write no-ops.
        await refresher.RefreshAsync([Guid.NewGuid()], CancellationToken.None);
        await refresher.RemoveIntroSkipperSegmentsAsync([Guid.NewGuid()], CancellationToken.None);

        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(store.ReplacedItems);
        Assert.Empty(store.DeletedOwnItemIds);
    }

    [Fact]
    public async Task RemoveIntroSkipperSegmentsAsync_DelegatesIdsToStore()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        var refresher = CreateRefresher(store);

        await refresher.RemoveIntroSkipperSegmentsAsync([firstId, secondId], CancellationToken.None);

        Assert.Equal([firstId, secondId], store.DeletedOwnItemIds);
        Assert.Empty(store.ReplacedItems);
    }

    [Fact]
    public async Task RemoveIntroSkipperSegmentsAsync_PropagatesDeleteFailure()
    {
        var expectedException = new InvalidOperationException("boom");
        var store = new FakeJellyfinSegmentStore
        {
            DeleteOwnException = expectedException
        };
        var refresher = CreateRefresher(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => refresher.RemoveIntroSkipperSegmentsAsync([Guid.NewGuid()], CancellationToken.None));

        Assert.Same(expectedException, exception);
    }

    // RefreshAsync resolves ids through the library manager and reads MaxParallelism
    // from the plugin configuration, so refresh tests scope a plugin instance and
    // register the refreshed item.
    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope()
        => new(EntrypointTestHelpers.CreateTempCacheDir());

    private static MediaSegmentRefreshService CreateRefresher(
        FakeJellyfinSegmentStore store,
        Guid? libraryItemId = null,
        SegmentDtoFactory? segmentDtoFactory = null)
        => new(
            new MediaSegmentMirror(store, segmentDtoFactory ?? new SegmentDtoFactory(DatabaseTestHelpers.CreateTempSegmentDatabase())),
            libraryItemId is { } itemId
                ? EntrypointTestHelpers.CreateLibraryManager(CreateMovie(itemId))
                : EntrypointTestHelpers.CreateLibraryManager(),
            NullLogger<MediaSegmentRefreshService>.Instance);

    private static Movie CreateMovie(Guid itemId)
    {
        var item = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(item, "Id", itemId);
        EntrypointTestHelpers.EnsureNonVirtual(item);
        return item;
    }

    /// <summary>
    /// A previously mirrored Jellyfin row for tests whose plugin database is empty:
    /// it makes the sync's intended push (nothing) differ from the mirror, so the
    /// write path under test actually runs instead of skipping as a no-op.
    /// </summary>
    private static MediaSegmentDto CreateMirroredDto(Guid itemId)
        => new()
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            Type = MediaSegmentType.Intro,
            StartTicks = TickConversions.FromSeconds(100),
            EndTicks = TickConversions.FromSeconds(160)
        };
}
