// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Helper;
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
        var itemId = Guid.NewGuid();
        await mirror.SyncItemAsync(itemId, CancellationToken.None);
        await service.DeleteUncorrelatedSegmentAsync(
            itemId,
            AnalysisMode.Introduction,
            CreateSegment(MediaSegmentType.Intro, 10, 20, Guid.NewGuid(), itemId),
            CancellationToken.None);

        // The correlated delete still commits the plugin tombstone; the mirror's
        // disabled no-op must not read as drift and trigger a resync.
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.NotNull(await service.DeleteSegmentAsync(itemId, row.Id, CancellationToken.None));

        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(store.ReplacedItems);
        Assert.Empty(store.DeletedSegments);
    }

    [Fact]
    public async Task DeleteSegmentAsync_CorrelatedRowFound_DeletesTargetedWithoutResync()
    {
        using var scope = CreatePluginScope();
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        var store = new FakeJellyfinSegmentStore();
        var service = DatabaseTestHelpers.CreateEditorService(store, database);

        var deleted = await service.DeleteSegmentAsync(itemId, row.Id, CancellationToken.None);

        Assert.NotNull(deleted);
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);
        // The shared id found its Jellyfin row: the targeted delete suffices, no full
        // mirror replace runs on the normal path.
        Assert.Empty(store.ReplacedItems);
    }

    [Fact]
    public async Task DeleteSegmentAsync_MissingJellyfinRow_ResyncsMirror()
    {
        using var scope = CreatePluginScope();
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        var store = new FakeJellyfinSegmentStore { MissingSegmentIds = [row.Id] };
        var service = DatabaseTestHelpers.CreateEditorService(store, database);

        var deleted = await service.DeleteSegmentAsync(itemId, row.Id, CancellationToken.None);

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

    [Fact]
    public async Task DeleteUncorrelatedSegmentAsync_MatchesWithinOneTick_TombstonesAndResyncs()
    {
        using var scope = CreatePluginScope();
        var itemId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        // Imported rows are rounded from seconds (1238398 ticks for 0.12383975...);
        // the pre-upgrade mirror row of the same value was truncated to 1238397.
        var pluginRow = await database.AddUserSegmentAsync(itemId, AnalysisMode.Introduction, 1238398, 500000000);
        var store = new FakeJellyfinSegmentStore();
        var service = DatabaseTestHelpers.CreateEditorService(store, database);
        var jellyfinRowId = Guid.NewGuid();

        await service.DeleteUncorrelatedSegmentAsync(
            itemId,
            AnalysisMode.Introduction,
            CreateSegment(MediaSegmentType.Intro, 1238397, 500000000, jellyfinRowId, itemId),
            CancellationToken.None);

        // The one-tick-off counterpart is the plugin row: deleted (user row, hard delete),
        // the targeted Jellyfin delete hits the uncorrelated id, and because that id is not
        // the plugin row's, the item's mirror is re-synced so a row mirrored under the
        // plugin id cannot linger.
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal([(itemId, jellyfinRowId)], store.DeletedSegments);
        var (syncedItemId, pushed) = Assert.Single(store.ReplacedItems);
        Assert.Equal(itemId, syncedItemId);
        Assert.Empty(pushed);
        Assert.NotEqual(pluginRow.Id, jellyfinRowId);
    }

    [Fact]
    public async Task DeleteOwnSegments_WaitsForParkedSyncOfSameItem()
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

        // Park a sync between its plugin-database read and its store replace, holding
        // the item's stripe — the stale-cleanup adversary: if the bulk delete ran now,
        // the stale replace would land after it and resurrect rows whose plugin source
        // the cleanup caller is about to remove.
        var sync = mirror.SyncItemAsync(itemId, CancellationToken.None);
        await writeEntered.Task;

        var cleanup = mirror.DeleteOwnSegmentsAsync([itemId], CancellationToken.None);

        // The cleanup must serialize behind the parked sync instead of interleaving.
        Assert.NotSame(cleanup, await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromMilliseconds(250))));
        Assert.Empty(store.DeletedOwnItemIds);

        store.WriteGate!.SetResult();
        await sync;
        await cleanup;

        // The stale replace landed first and the delete last, so the item converges
        // to no Jellyfin rows.
        Assert.Single(store.ReplacedItems);
        Assert.Equal([itemId], store.DeletedOwnItemIds);
    }

    [Fact]
    public async Task DeleteSegmentAsync_CancellationAfterCommittedJellyfinDelete_StillResetsAnalyzedState()
    {
        var itemId = Guid.NewGuid();
        using var scope = EntrypointTestHelpers.CreateMoviePluginScope(itemId, updateMediaSegments: true, out _);
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [itemId], "hash");

        // Cancel at the exact point where the Jellyfin delete has committed: the
        // cascade cannot be retried (a repeated DELETE 404s on the tombstoned row),
        // so the analyzed-state reset must complete despite the cancellation or the
        // item stays marked analyzed forever.
        using var cts = new CancellationTokenSource();
        var store = new FakeJellyfinSegmentStore { DeleteSegmentCallback = () => cts.Cancel() };
        var service = DatabaseTestHelpers.CreateEditorService(store, database);

        var deleted = await service.DeleteSegmentAsync(itemId, row.Id, cts.Token);

        Assert.NotNull(deleted);
        Assert.Equal([(itemId, row.Id)], store.DeletedSegments);
        var snapshot = await database.GetSeasonQueueSnapshotAsync(itemId, [itemId]);
        Assert.False(snapshot.AnalyzedConfigHashes.ContainsKey((itemId, AnalysisMode.Introduction)));
    }

    /// <summary>
    /// Scopes a plugin instance so the mirror policy can read the (default, mirroring
    /// enabled) configuration.
    /// </summary>
    private static EntrypointTestHelpers.PluginInstanceScope CreatePluginScope()
        => new(EntrypointTestHelpers.CreateTempCacheDir());

    /// <summary>
    /// Picks an id on a different lock stripe than <paramref name="other"/> (the mirror
    /// and mutation pools share the stripe mapping) so cross-item concurrency assertions
    /// cannot flake on a stripe collision.
    /// </summary>
    private static Guid NewGuidOnDifferentStripe(Guid other)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (StripedAsyncLock.StripeIndex(id) == StripedAsyncLock.StripeIndex(other));

        return id;
    }

    private static MediaSegmentDto CreateSegment(MediaSegmentType type, long startTicks, long endTicks, Guid id, Guid itemId)
        => new()
        {
            Id = id,
            ItemId = itemId,
            Type = type,
            StartTicks = startTicks,
            EndTicks = endTicks
        };
}
