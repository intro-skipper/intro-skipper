// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using Xunit;

/// <summary>
/// Tests for <see cref="MediaSegmentMirror"/>: uniform per-item convergence, per-item
/// serialization, cross-item concurrency, and the disabled no-op.
/// </summary>
public sealed class TestMediaSegmentMirror
{
    [Fact]
    public async Task SyncItem_UsesUniformReplace_ForEveryMode()
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
    public async Task SyncItem_SerializesConcurrentCallsForSameItem()
    {
        var itemId = Guid.NewGuid();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            WriteEntered = writeEntered,
            BlockedItemId = itemId
        };
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        var mirror = DatabaseTestHelpers.CreateMirror(store, database);

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

        // The first call converged the mirror, so the serialized second call found
        // nothing left to change and skipped its write.
        Assert.Equal(1, store.WriteCallCount);
        Assert.Single(store.ReplacedItems);
    }

    [Fact]
    public async Task SyncItem_AllowsConcurrentCallsForDifferentItems()
    {
        var firstItemId = Guid.NewGuid();
        var secondItemId = NewGuidOnDifferentStripe(firstItemId);
        var store = new FakeJellyfinSegmentStore
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockedItemId = firstItemId
        };
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

        // Seeding gives each item a segment to push (an empty-vs-empty sync would skip
        // the store) and warms EF/SQLite before the race: the 1-second completion window
        // below asserts mirror-stripe independence and must not also absorb the one-time
        // model build + migration, which alone exceeds it on a cold run.
        await database.ReplaceAutoSegmentsAsync(
            firstItemId, AnalysisMode.Introduction, [new Segment(firstItemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        await database.ReplaceAutoSegmentsAsync(
            secondItemId, AnalysisMode.Introduction, [new Segment(secondItemId, new TimeRange(10, 20))], SegmentSource.Chapter);
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
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(
            Plugin.Instance!,
            "Configuration",
            new IntroSkipper.Configuration.PluginConfiguration { UpdateMediaSegments = false });
        var itemId = Guid.NewGuid();
        var store = new FakeJellyfinSegmentStore();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
        var mirror = DatabaseTestHelpers.CreateMirror(store, database);

        // The mirror flag lives in the mirror, not at call sites: every operation
        // reports the typed disabled outcome and leaves the store untouched.
        Assert.Equal(MirrorSyncOutcome.MirroringDisabled, await mirror.SyncItemAsync(itemId, CancellationToken.None));
        Assert.Equal(
            MirrorDeleteOutcome.MirroringDisabled,
            await mirror.DeleteValidatedSegmentAsync(itemId, Guid.NewGuid(), MediaSegmentType.Intro, 10, 20, CancellationToken.None));

        Assert.Equal(0, store.WriteCallCount);
        Assert.Empty(store.ReplacedItems);
        Assert.Empty(store.DeletedSegments);
    }

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
}
