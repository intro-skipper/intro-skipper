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
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestMediaSegmentRefreshService
{
    [Fact]
    public async Task RefreshAsync_AwaitsSegmentStoreWrite_BeforeCompleting()
    {
        var itemId = Guid.NewGuid();
        var item = CreateMovie(itemId);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJellyfinSegmentStore
        {
            WriteEntered = writeEntered,
            WriteGate = writeGate,
            BlockedItemId = itemId
        };
        var refresher = CreateRefresher(store);

        var refreshTask = refresher.RefreshAsync(item, CancellationToken.None);

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

        var store = new FakeJellyfinSegmentStore();
        var refresher = CreateRefresher(store, segmentDtoFactory: new SegmentDtoFactory(database));

        await refresher.RefreshAsync(CreateMovie(itemId), CancellationToken.None);

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
    public async Task RefreshAsync_LogsAndReturnsAfterStoreFailure()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            WriteException = new InvalidOperationException("boom")
        };
        var refresher = CreateRefresher(store);

        await refresher.RefreshAsync(CreateMovie(itemId), CancellationToken.None);

        Assert.Equal(1, store.WriteCallCount);
        var (replacedItemId, _) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
    }

    [Fact]
    public async Task RefreshAsync_RethrowsCriticalException()
    {
        var store = new FakeJellyfinSegmentStore
        {
            WriteException = new ThreadInterruptedException()
        };
        var refresher = CreateRefresher(store);

        await Assert.ThrowsAsync<ThreadInterruptedException>(
            () => refresher.RefreshAsync(CreateMovie(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(1, store.WriteCallCount);
    }

    [Fact]
    public async Task RefreshAsync_ByIds_ResolvesItemsViaLibraryManager_SkippingEmptyAndDuplicateIds()
    {
        var itemId = Guid.NewGuid();
        var item = CreateMovie(itemId);
        var store = new FakeJellyfinSegmentStore();
        var libraryManager = EntrypointTestHelpers.CreateLibraryManager(item);
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { MaxParallelism = 2 });
        var refresher = CreateRefresher(store, libraryManager);

        await refresher.RefreshAsync([itemId, Guid.Empty, itemId], CancellationToken.None);

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
        await refresher.RefreshAsync(CreateMovie(Guid.NewGuid()), CancellationToken.None);
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

    private static MediaSegmentRefreshService CreateRefresher(
        FakeJellyfinSegmentStore store,
        ILibraryManager? libraryManager = null,
        SegmentDtoFactory? segmentDtoFactory = null)
        => new(
            store,
            new MediaSegmentMirror(store, segmentDtoFactory ?? new SegmentDtoFactory(DatabaseTestHelpers.CreateTempSegmentDatabase())),
            libraryManager ?? EntrypointTestHelpers.CreateLibraryManager(),
            NullLogger<MediaSegmentRefreshService>.Instance);

    private static Movie CreateMovie(Guid itemId)
    {
        var item = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(item, "Id", itemId);
        EntrypointTestHelpers.EnsureNonVirtual(item);
        return item;
    }
}
