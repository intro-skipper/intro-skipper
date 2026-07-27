// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
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
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 160)), AnalysisMode.Introduction);
        await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(1200, 1260)), AnalysisMode.Credits);
        var store = new FakeJellyfinSegmentStore();
        var refresher = CreateRefresher(store, segmentDtoFactory: new SegmentDtoFactory(database));

        await refresher.RefreshAsync(CreateMovie(itemId), CancellationToken.None);

        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Equal(2, pushed.Count);
        var intro = Assert.Single(pushed, segment => segment.Type == MediaSegmentType.Intro);
        Assert.Equal(itemId, intro.ItemId);
        Assert.Equal(TimeSpan.FromSeconds(100).Ticks, intro.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(160).Ticks, intro.EndTicks);
        var outro = Assert.Single(pushed, segment => segment.Type == MediaSegmentType.Outro);
        Assert.Equal(TimeSpan.FromSeconds(1200).Ticks, outro.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(1260).Ticks, outro.EndTicks);
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
    public async Task RemoveIntroSkipperSegmentsAsync_WaitsForItemLease_BeforeDeleting()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        var refresher = CreateRefresher(store);

        var lease = await MediaSegmentItemLock.AcquireAsync(itemId, CancellationToken.None);
        var removal = refresher.RemoveIntroSkipperSegmentsAsync([itemId], CancellationToken.None);

        // Bounded observation window: the delete must not run while another writer, such
        // as an in-flight refresh or editor replace, still holds the item's lease.
        Assert.NotSame(removal, await Task.WhenAny(removal, Task.Delay(TimeSpan.FromMilliseconds(250))));
        Assert.Empty(store.DeletedOwnItemIds);

        lease.Dispose();
        await removal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([itemId], store.DeletedOwnItemIds);
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

    [Fact]
    public async Task RefreshAsync_WaitsForEditorReplacementOfSameItem()
    {
        var item = CreateMovie(Guid.NewGuid());
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJellyfinSegmentStore
        {
            WriteEntered = writeEntered,
            WriteGate = writeGate,
            GateOnlyFirstWrite = true,
            BlockedItemId = item.Id
        };
        var editor = new MediaSegmentEditorService(store, database, [], NullLogger<MediaSegmentEditorService>.Instance);
        var refresher = CreateRefresher(store, segmentDtoFactory: new SegmentDtoFactory(database));

        var replacement = editor.ReplaceEditorSegmentsAsync(
            item,
            Guid.NewGuid(),
            [],
            [AnalysisMode.Introduction],
            CancellationToken.None);
        await writeEntered.Task;

        var refresh = refresher.RefreshAsync(item, CancellationToken.None);

        Assert.NotSame(refresh, await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromMilliseconds(250))));
        Assert.Equal(1, store.WriteCallCount);

        writeGate.SetResult();
        await replacement;
        await refresh;

        Assert.Equal(2, store.WriteCallCount);
    }

    private static MediaSegmentRefreshService CreateRefresher(
        FakeJellyfinSegmentStore store,
        ILibraryManager? libraryManager = null,
        SegmentDtoFactory? segmentDtoFactory = null)
        => new(
            store,
            segmentDtoFactory ?? new SegmentDtoFactory(DatabaseTestHelpers.CreateTempSegmentDatabase()),
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
