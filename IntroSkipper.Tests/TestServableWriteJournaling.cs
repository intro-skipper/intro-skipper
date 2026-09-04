// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
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
    private readonly string _dbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-journaling.db");

    [Fact]
    public async Task ReplaceAutoSegments_JournalsOnlyWhenTheImageChanges()
    {
        var itemId = Guid.NewGuid();
        var database = CreateDatabase();

        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter, "hash");
        Assert.Equal([itemId], await QueuedItemIdsAsync());

        // An identical rewrite keeps the row in place (stable id): nothing servable
        // changed, so no new work is journaled.
        await ClearQueueAsync();
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter, "hash-2");
        Assert.Empty(await QueuedItemIdsAsync());

        // A fully rejected write leaves the standing rows untouched and journals nothing.
        await database.SeedUserSegmentAsync(itemId, AnalysisMode.Credits, DatabaseTestHelpers.Ticks(30), DatabaseTestHelpers.Ticks(40));
        await ClearQueueAsync();
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(30, 40))], SegmentSource.Chapter, "hash");
        Assert.Empty(await QueuedItemIdsAsync());
    }

    [Fact]
    public async Task EraseItems_JournalsEveryAddressedItem()
    {
        var withRows = Guid.NewGuid();
        var withoutRows = Guid.NewGuid();
        var database = CreateDatabase();
        await database.SeedUserSegmentAsync(withRows, AnalysisMode.Introduction, 10, 20);
        await ClearQueueAsync();

        // The zero-row item is journaled too: it may hold ghost Jellyfin rows that
        // only a projection heals, and the erase names it explicitly.
        await database.EraseItemsAsync([withRows, withoutRows]);

        Assert.Equal(new[] { withRows, withoutRows }.OrderBy(id => id).ToArray(), await QueuedItemIdsAsync());
    }

    [Fact]
    public async Task DeleteSegmentsByMode_JournalsEveryAffectedItem()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        var database = CreateDatabase();
        await database.SeedUserSegmentAsync(first, AnalysisMode.Introduction, 10, 20);
        await database.SeedUserSegmentAsync(second, AnalysisMode.Introduction, 30, 40);
        await database.SeedUserSegmentAsync(untouched, AnalysisMode.Credits, 50, 60);
        await ClearQueueAsync();

        await database.DeleteSegmentsByModeAsync(AnalysisMode.Introduction);

        Assert.Equal(new[] { first, second }.OrderBy(id => id).ToArray(), await QueuedItemIdsAsync());
    }

    [Fact]
    public async Task ResetItemsForReanalysis_JournalsOnlyItemsWithDeletedRows()
    {
        var automaticItem = Guid.NewGuid();
        var userItem = Guid.NewGuid();
        var database = CreateDatabase();
        await database.ReplaceAutoSegmentsAsync(automaticItem, AnalysisMode.Introduction, [new Segment(automaticItem, new TimeRange(10, 20))], SegmentSource.Chapter, "hash");

        // The user item's automatic row is shielded by its active user row of the
        // same mode, so the reset deletes nothing there and journals nothing.
        await database.SeedUserSegmentAsync(userItem, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(10), DatabaseTestHelpers.Ticks(20));
        await ClearQueueAsync();

        await database.ResetItemsForReanalysisAsync([automaticItem, userItem], [AnalysisMode.Introduction]);

        Assert.Equal([automaticItem], await QueuedItemIdsAsync());
    }

    [Fact]
    public async Task CleanStaleAutomaticSegments_JournalsOnlyItemsWithRemovedRows()
    {
        var staleItem = Guid.NewGuid();
        var freshItem = Guid.NewGuid();
        var database = CreateDatabase();
        await database.ReplaceAutoSegmentsAsync(staleItem, AnalysisMode.Introduction, [new Segment(staleItem, new TimeRange(10, 20))], SegmentSource.Chapter, "old-hash");
        await database.ReplaceAutoSegmentsAsync(freshItem, AnalysisMode.Introduction, [new Segment(freshItem, new TimeRange(10, 20))], SegmentSource.Chapter, "current-hash");
        await ClearQueueAsync();

        var removed = await database.CleanStaleAutomaticSegmentsAsync([staleItem, freshItem], AnalysisMode.Introduction, "current-hash");

        Assert.Equal(1, removed);
        Assert.Equal([staleItem], await QueuedItemIdsAsync());
    }

    [Fact]
    public async Task BulkJournaling_SupersedesRecordedBackoff()
    {
        var itemId = Guid.NewGuid();
        var database = CreateDatabase();
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter, "hash");

        // A marker parked on backoff after a projection failure becomes due
        // immediately when new work lands, like the intent path's enqueue.
        await using (var db = CreateContext())
        {
            var queue = Assert.Single(await db.ProjectionQueue.ToListAsync());
            queue.NextAttemptAt = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }

        await database.EraseItemsAsync([itemId]);

        await using var verify = CreateContext();
        var marker = Assert.Single(await verify.ProjectionQueue.ToListAsync());
        Assert.Null(marker.NextAttemptAt);
        Assert.True(marker.Version > 1);
    }

    /// <inheritdoc/>
    public void Dispose() => DatabaseTestHelpers.DeleteSqliteFiles(_dbPath);

    private IntroSkipperDatabase CreateDatabase() => DatabaseTestHelpers.CreateSegmentDatabase(_dbPath);

    private IntroSkipperDbContext CreateContext() => DatabaseTestHelpers.CreateSegmentContext(_dbPath);

    private async Task<Guid[]> QueuedItemIdsAsync()
    {
        await using var db = CreateContext();
        return await db.ProjectionQueue.Select(q => q.ItemId).OrderBy(id => id).ToArrayAsync();
    }

    private async Task ClearQueueAsync()
    {
        await using var db = CreateContext();
        await db.ProjectionQueue.ExecuteDeleteAsync();
    }
}
