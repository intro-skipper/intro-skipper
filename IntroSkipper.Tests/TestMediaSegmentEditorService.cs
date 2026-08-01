// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using Xunit;

public sealed class TestMediaSegmentEditorService
{
    [Fact]
    public async Task MirrorSyncItem_UsesUniformReplace_ForEveryMode()
    {
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(1200, 1260))], SegmentSource.Chromaprint);
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Commercial,
            [new Segment(itemId, new TimeRange(300, 330)), new Segment(itemId, new TimeRange(600, 630))],
            SegmentSource.BlackFrame);
        await database.AddUserSegmentAsync(
            itemId, AnalysisMode.Recap, TickConversions.FromSeconds(20), TickConversions.FromSeconds(40));
        var rows = await database.GetSegmentsAsync(itemId);
        Assert.Equal(5, rows.Count);

        var store = new FakeJellyfinSegmentStore();
        var mirror = DatabaseTestHelpers.CreateMirror(store, database);

        await mirror.SyncItemAsync(itemId, CancellationToken.None);

        // One uniform replace carries every active segment of every mode — no per-type
        // routing, no commercial special case — and each DTO reuses its plugin row's id.
        Assert.Equal(1, store.WriteCallCount);
        var (replacedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, replacedItemId);
        Assert.Equal(rows.Count, pushed.Count);
        foreach (var row in rows)
        {
            var dto = Assert.Single(pushed, segment => segment.Id == row.Id);
            Assert.Equal(itemId, dto.ItemId);
            Assert.Equal(row.StartTicks, dto.StartTicks);
            Assert.Equal(row.EndTicks, dto.EndTicks);
        }

        Assert.Equal(2, pushed.Count(segment => segment.Type == MediaSegmentType.Commercial));
    }

    [Fact]
    public async Task MirrorSyncItem_SerializesConcurrentCallsForSameItem()
    {
        var itemId = Guid.NewGuid();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteEntered = writeEntered,
            BlockedItemId = itemId
        };
        var mirror = DatabaseTestHelpers.CreateMirror(store, DatabaseTestHelpers.CreateTempSegmentDatabase());

        // First call enters the critical section and parks inside the store write while
        // holding the lock; wait for the park so the assertions below cannot race the
        // asynchronous DTO-factory read that precedes the write.
        var first = mirror.SyncItemAsync(itemId, CancellationToken.None);
        await writeEntered.Task;

        // Second call for the same item must block on the per-item lock and therefore must
        // not have reached the store yet.
        var second = mirror.SyncItemAsync(itemId, CancellationToken.None);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, store.WriteCallCount);

        store.WriteGate!.SetResult();
        await first;
        await second;

        Assert.Equal(2, store.WriteCallCount);
        Assert.Equal(2, store.ReplacedItems.Count);
    }

    [Fact]
    public async Task MirrorSyncItem_AllowsConcurrentCallsForDifferentItems()
    {
        var firstItemId = Guid.NewGuid();
        var secondItemId = NewGuidOnDifferentStripe(firstItemId);
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = firstItemId
        };
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // Warm EF/SQLite initialization before the race: the 1-second completion window
        // below asserts mirror-stripe independence and must not also absorb the one-time
        // model build + migration, which alone exceeds it on a cold run.
        await database.GetSegmentsAsync(Guid.NewGuid());
        var mirror = DatabaseTestHelpers.CreateMirror(store, database);

        var first = mirror.SyncItemAsync(firstItemId, CancellationToken.None);
        var second = mirror.SyncItemAsync(secondItemId, CancellationToken.None);

        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.False(first.IsCompleted);

        store.WriteGate!.SetResult();
        await first;

        Assert.Equal(2, store.WriteCallCount);
    }

    [Fact]
    public async Task Writes_DoNotTouchJellyfin_WhenUpdateMediaSegmentsDisabled()
    {
        using var scope = CreatePluginScope();
        EntrypointTestHelpers.SetPropertyOrField(
            Plugin.Instance!,
            "Configuration",
            new IntroSkipper.Configuration.PluginConfiguration { UpdateMediaSegments = false });
        var store = new FakeJellyfinSegmentStore();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var mirror = DatabaseTestHelpers.CreateMirror(store, database);
        var service = DatabaseTestHelpers.CreateEditorService(store, database);

        // The mirror flag lives in the mirror and the services, not at call sites:
        // every write no-ops.
        await mirror.SyncItemAsync(Guid.NewGuid(), CancellationToken.None);
        await service.DeleteStoredSegmentAsync(Guid.NewGuid(), AnalysisMode.Introduction, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(store.ReplacedItems);
        Assert.Empty(store.DeletedSegments);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsMatchingSegment()
    {
        var itemId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments =
            [
                CreateSegment(MediaSegmentType.Outro, 30, 40, Guid.NewGuid(), itemId),
                CreateSegment(MediaSegmentType.Intro, 10, 20, segmentId, itemId)
            ]
        };
        var service = CreateService(store);

        var result = await service.GetSegmentAsync(itemId, segmentId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(segmentId, result!.Id);
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsNull_WhenItemIdDoesNotMatch()
    {
        var segmentId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, segmentId, Guid.NewGuid())]
        };
        var service = CreateService(store);

        Assert.Null(await service.GetSegmentAsync(Guid.NewGuid(), segmentId, CancellationToken.None));
    }

    [Fact]
    public async Task GetSegmentAsync_ReturnsNull_WhenSegmentIdDoesNotMatch()
    {
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore
        {
            ExistingSegments = [CreateSegment(MediaSegmentType.Intro, 10, 20, Guid.NewGuid(), itemId)]
        };
        var service = CreateService(store);

        var result = await service.GetSegmentAsync(itemId, Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteStoredSegmentAsync_CorrelatedRowFound_DeletesTargetedWithoutResync()
    {
        using var scope = CreatePluginScope();
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        var store = new FakeJellyfinSegmentStore();
        var service = CreateService(store, database);

        var deleted = await service.DeleteStoredSegmentAsync(
            itemId, AnalysisMode.Introduction, row.Id, row.Id, CancellationToken.None);

        Assert.NotNull(deleted);
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);
        // The shared id found its Jellyfin row: the targeted delete suffices, no full
        // mirror replace runs on the normal path.
        Assert.Empty(store.ReplacedItems);
    }

    [Fact]
    public async Task DeleteStoredSegmentAsync_MissingJellyfinRow_ResyncsMirror()
    {
        using var scope = CreatePluginScope();
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        var store = new FakeJellyfinSegmentStore { MissingSegmentIds = [row.Id] };
        var service = CreateService(store, database);

        var deleted = await service.DeleteStoredSegmentAsync(
            itemId, AnalysisMode.Introduction, row.Id, row.Id, CancellationToken.None);

        // A correlated plugin row with no Jellyfin row under the shared id is the drift
        // signal (server stopped preserving provider ids, or the row was already gone):
        // the cascade re-converges the whole item mirror instead of leaving stale rows.
        Assert.NotNull(deleted);
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);
        var (syncedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, syncedItemId);
        // The deleted intro was tombstoned, so the re-sync pushes the empty active set.
        Assert.Empty(pushed);
    }

    private static MediaSegmentEditorService CreateService(
        FakeJellyfinSegmentStore store,
        IntroSkipper.Db.IIntroSkipperDatabase? database = null)
        => DatabaseTestHelpers.CreateEditorService(store, database ?? DatabaseTestHelpers.CreateTempSegmentDatabase());

    /// <summary>
    /// Scopes a plugin instance with an empty library manager so the delete cascade's
    /// item lookup (for the season-state reset) resolves to null instead of crashing.
    /// </summary>
    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope()
    {
        var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        return scope;
    }

    /// <summary>
    /// Picks an id on a different mirror lock stripe than <paramref name="other"/> so
    /// cross-item concurrency assertions cannot flake on a stripe collision.
    /// </summary>
    private static Guid NewGuidOnDifferentStripe(Guid other)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (MediaSegmentMirror.StripeIndex(id) == MediaSegmentMirror.StripeIndex(other));

        return id;
    }

    private static MediaSegmentDto CreateSegment(MediaSegmentType type, long startTicks, long endTicks, Guid id = default, Guid itemId = default)
        => new()
        {
            Id = id,
            ItemId = itemId,
            Type = type,
            StartTicks = startTicks,
            EndTicks = endTicks
        };
}
