// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Every facade write that changes an item's servable image must journal the item's
/// projection in the same transaction — the durability contract that lets analysis
/// and maintenance writers drop their own mirror pushes — and writes that change
/// nothing servable must journal nothing, so the worker never chases no-ops.
/// </summary>
public sealed class TestServableWriteJournaling : IDisposable
{
    private readonly SegmentChangeHarness _h = new();

    [Fact]
    public async Task ReplaceAutoSegments_JournalsOnlyWhenTheImageChanges()
    {
        var itemId = Guid.NewGuid();
        var database = _h.Database;

        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter, "hash");
        Assert.Equal([itemId], await _h.QueuedItemIdsAsync());

        // An identical rewrite keeps the row in place (stable id): nothing servable
        // changed, so no new work is journaled.
        await _h.ClearQueueAsync();
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter, "hash-2");
        Assert.Empty(await _h.QueuedItemIdsAsync());

        // A fully rejected write leaves the standing rows untouched and journals nothing.
        await database.SeedUserSegmentAsync(itemId, AnalysisMode.Credits, DatabaseTestHelpers.Ticks(30), DatabaseTestHelpers.Ticks(40));
        await _h.ClearQueueAsync();
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(30, 40))], SegmentSource.Chapter, "hash");
        Assert.Empty(await _h.QueuedItemIdsAsync());
    }

    [Fact]
    public async Task EraseItems_JournalsEveryAddressedItem()
    {
        var withAutomaticRow = Guid.NewGuid();
        var withUserRow = Guid.NewGuid();
        var withoutRows = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(withAutomaticRow, AnalysisMode.Introduction, [new Segment(withAutomaticRow, new TimeRange(10, 20))], SegmentSource.Chapter);
        await database.SeedUserSegmentAsync(withUserRow, AnalysisMode.Introduction, 10, 20);
        await _h.ClearQueueAsync();

        // The erase deletes nothing of the last two items and journals them too. Jellyfin
        // clears a file-changed item's mirror after the provider's erase, so a kept user
        // row needs a push back, and an item without rows may hold ghost Jellyfin rows
        // that only a projection heals.
        await database.EraseItemsAsync([withAutomaticRow, withUserRow, withoutRows], eraseTombstones: true);

        Assert.Equal(new[] { withAutomaticRow, withUserRow, withoutRows }.Order(), await _h.QueuedItemIdsAsync());
    }

    [Fact]
    public async Task DeleteSegmentsByMode_JournalsEveryAffectedItem()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        var database = _h.Database;

        // The first item loses its automatic row. The second keeps its user row and is
        // journaled anyway, so a diverged mirror of the mode converges.
        await database.ReplaceAutoSegmentsAsync(first, AnalysisMode.Introduction, [new Segment(first, new TimeRange(10, 20))], SegmentSource.Chapter);
        await database.SeedUserSegmentAsync(second, AnalysisMode.Introduction, 30, 40);
        await database.ReplaceAutoSegmentsAsync(untouched, AnalysisMode.Credits, [new Segment(untouched, new TimeRange(50, 60))], SegmentSource.Chapter);
        await _h.ClearQueueAsync();

        await database.DeleteSegmentsByModeAsync(AnalysisMode.Introduction, eraseTombstones: true);

        Assert.Equal(new[] { first, second }.OrderBy(id => id).ToArray(), await _h.QueuedItemIdsAsync());
    }

    [Fact]
    public async Task DeleteSegmentsByMode_PreservesTombstoneAndJournalsItsItemWhenRequested()
    {
        var itemId = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chromaprint);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        await database.DeleteSegmentAsync(itemId, row.Id);
        await _h.ClearQueueAsync();

        var erasedItemIds = await database.DeleteSegmentsByModeAsync(AnalysisMode.Introduction, eraseTombstones: false);

        Assert.Equal([itemId], erasedItemIds);
        var preserved = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(SegmentState.Suppressed, preserved.State);
        Assert.Equal([itemId], await _h.QueuedItemIdsAsync());
    }

    [Fact]
    public async Task ResetItemsForReanalysis_JournalsOnlyItemsWithDeletedRows()
    {
        var automaticItem = Guid.NewGuid();
        var userItem = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(automaticItem, AnalysisMode.Introduction, [new Segment(automaticItem, new TimeRange(10, 20))], SegmentSource.Chapter, "hash");

        // The user item's automatic row is shielded by its active user row of the
        // same mode, so the reset deletes nothing there and journals nothing.
        await database.SeedUserSegmentAsync(userItem, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(10), DatabaseTestHelpers.Ticks(20));
        await _h.ClearQueueAsync();

        await database.ResetItemsForReanalysisAsync([automaticItem, userItem], [AnalysisMode.Introduction]);

        Assert.Equal([automaticItem], await _h.QueuedItemIdsAsync());
    }

    [Fact]
    public async Task CleanStaleAutomaticSegments_JournalsOnlyItemsWithRemovedRows()
    {
        var staleItem = Guid.NewGuid();
        var freshItem = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(staleItem, AnalysisMode.Introduction, [new Segment(staleItem, new TimeRange(10, 20))], SegmentSource.Chapter, "old-hash");
        await database.ReplaceAutoSegmentsAsync(freshItem, AnalysisMode.Introduction, [new Segment(freshItem, new TimeRange(10, 20))], SegmentSource.Chapter, "current-hash");
        await _h.ClearQueueAsync();

        var removed = await database.CleanStaleAutomaticSegmentsAsync([staleItem, freshItem], AnalysisMode.Introduction, "current-hash");

        Assert.Equal(1, removed);
        Assert.Equal([staleItem], await _h.QueuedItemIdsAsync());
    }

    [Fact]
    public async Task BulkJournaling_SupersedesRecordedBackoff()
    {
        var itemId = Guid.NewGuid();
        var database = _h.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter, "hash");

        // A marker parked on backoff after a projection failure becomes due
        // immediately when new work lands, like the intent path's enqueue.
        await using (var db = _h.Context())
        {
            var queue = Assert.Single(await db.ProjectionQueue.ToListAsync());
            queue.NextAttemptAt = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }

        await database.EraseItemsAsync([itemId], eraseTombstones: true);

        await using var verify = _h.Context();
        var marker = Assert.Single(await verify.ProjectionQueue.ToListAsync());
        Assert.Null(marker.NextAttemptAt);
        Assert.True(marker.Version > 1);
    }

    /// <inheritdoc/>
    public void Dispose() => _h.Dispose();
}
