// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Providers;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IntroSkipper.Tests.DatabaseTestHelpers;

namespace IntroSkipper.Tests;

/// <summary>
/// Tombstone (suppressed segment) lifecycle: a user-deleted automatic segment is
/// remembered and its range never re-added by re-analysis (GitHub issue #863) until it is
/// restored, and stays out of the Jellyfin mirror.
/// </summary>
public sealed class TestSegmentTombstones : IDisposable
{
    private readonly TempSegmentDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task DeleteAutoSegment_Tombstones_AndHidesFromReads()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        var snapshot = await database.DeleteSegmentAsync(itemId, row.Id);

        Assert.NotNull(snapshot);
        Assert.Empty(await database.GetSegmentsAsync(itemId));
        var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(SegmentState.Suppressed, tombstone.State);
        Assert.Equal(SegmentSource.Chromaprint, tombstone.Source);

        // Deleting a tombstone again is a no-op.
        Assert.Null(await database.DeleteSegmentAsync(itemId, row.Id));
    }

    [Fact]
    public async Task DeleteUserSegment_HardDeletes_NoTombstoneRemains()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        var row = await database.SeedUserSegmentAsync(itemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));

        var snapshot = await database.DeleteSegmentAsync(itemId, row.Id);

        Assert.NotNull(snapshot);
        Assert.Equal(SegmentSource.User, snapshot!.Source);
        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
    }

    [Theory]
    [InlineData(10.0, 60.0, false)]  // identical range
    [InlineData(12.0, 61.0, false)]  // shifted re-detection
    [InlineData(20.0, 50.0, false)]  // a shorter piece of the deleted range
    [InlineData(10.0, 120.0, true)]  // a correction reaching well past the deleted end
    public async Task AnalysisWrite_OverATombstone_StoresOnlyACorrection(double newStart, double newEnd, bool expectedStored)
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        await database.DeleteSegmentAsync(itemId, row.Id);

        // Re-analysis must not resurrect the segment the user deleted, nor throw on the
        // unique index. A range reaching well past it is a correction and is stored.
        var stored = await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(newStart, newEnd))], SegmentSource.Chromaprint);

        Assert.Equal(expectedStored ? 1 : 0, stored);
        var allRows = await database.GetSegmentsAsync(itemId, includeSuppressed: true);
        Assert.Contains(allRows, segment =>
            segment.State == SegmentState.Suppressed
            && segment.StartTicks == Ticks(10)
            && segment.EndTicks == Ticks(60));
        var active = allRows.Where(segment => segment.State == SegmentState.Active).ToList();
        Assert.Equal(expectedStored ? 1 : 0, active.Count);
        if (expectedStored)
        {
            Assert.Equal((Ticks(newStart), Ticks(newEnd)), (active[0].StartTicks, active[0].EndTicks));
        }
    }

    [Fact]
    public async Task AnalysisWrite_FullyRejected_LeavesStandingRowsUntouched()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(10, 60)), new Segment(itemId, new TimeRange(100, 150))],
            SegmentSource.Chromaprint);
        var rows = await database.GetSegmentsAsync(itemId);
        var doomed = rows.Single(r => r.StartTicks == Ticks(100));
        await database.DeleteSegmentAsync(itemId, doomed.Id);

        // Re-analysis re-derives only a range the tombstone blocks: a fully rejected
        // write must leave the surviving row standing instead of clearing the mode.
        var stored = await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(102, 151))],
            SegmentSource.Chromaprint);

        Assert.Equal(0, stored);
        var allRows = await database.GetSegmentsAsync(itemId, includeSuppressed: true);
        Assert.Contains(allRows, segment =>
            segment.State == SegmentState.Suppressed
            && segment.StartTicks == Ticks(100)
            && segment.EndTicks == Ticks(150));
        var survivor = Assert.Single(allRows, segment => segment.State == SegmentState.Active);
        Assert.Equal(Ticks(10), survivor.StartTicks);
        Assert.Equal(Ticks(60), survivor.EndTicks);
    }

    // A mode erase keeps the user's own segments either way, and a kept tombstone goes on
    // refusing a shifted re-detection. Purging it lets detection add the range again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModeErase_KeptTombstoneKeepsRefusingReDetections_UserSegmentsStayEitherWay(bool eraseTombstones)
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);
        await database.DeleteSegmentAsync(itemId, Assert.Single(await database.GetSegmentsAsync(itemId)).Id);
        var user = await database.SeedUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(300), Ticks(330));

        await database.DeleteSegmentsByModeAsync(AnalysisMode.Introduction, eraseTombstones);

        Assert.Equal(user.Id, Assert.Single(await database.GetSegmentsAsync(itemId)).Id);
        Assert.Equal(eraseTombstones ? 1 : 2, (await database.GetSegmentsAsync(itemId, includeSuppressed: true)).Count);
        var stored = await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(12, 61))], SegmentSource.Chromaprint);
        Assert.Equal(eraseTombstones ? 1 : 0, stored);
    }

    [Fact]
    public async Task UpdateSegment_ReclaimsTombstonedRange_ByAbsorbingTombstone()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Commercial,
            [new Segment(itemId, new TimeRange(10, 20)), new Segment(itemId, new TimeRange(50, 60))],
            SegmentSource.Chapter);
        var rows = await database.GetSegmentsAsync(itemId);
        await database.DeleteSegmentAsync(itemId, rows[0].Id); // tombstone (10, 20)

        // Moving the other segment onto the tombstoned range is an explicit user
        // decision: the tombstone is absorbed instead of raising a phantom 409.
        var updated = await database.UpdateSegmentAsync(itemId, rows[1].Id, Ticks(10), Ticks(20));

        Assert.NotNull(updated);
        Assert.Equal(SegmentSource.User, updated!.Source);
        var remaining = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(rows[1].Id, remaining.Id);
        Assert.Equal(Ticks(10), remaining.StartTicks);
    }

    [Fact]
    public async Task RestoreSegment_ReactivatesWithOriginalSource()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.BlackFrame);
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));
        await database.DeleteSegmentAsync(itemId, row.Id);

        // A suppressed row addressed through the wrong item is unknown by contract.
        Assert.Null(await database.RestoreSegmentAsync(Guid.NewGuid(), row.Id));

        Assert.NotNull(await database.RestoreSegmentAsync(itemId, row.Id));
        Assert.Null(await database.RestoreSegmentAsync(itemId, row.Id)); // not suppressed anymore
        Assert.Null(await database.RestoreSegmentAsync(itemId, Guid.NewGuid()));

        var restored = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.BlackFrame, restored.Source);
        Assert.Equal(SegmentState.Active, restored.State);
    }

    [Fact]
    public async Task RestoreSegment_ReArmsClearedAnalysisRecordWithTheRowsHash()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint, "hash-1");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [itemId], "hash-1");
        var row = Assert.Single(await database.GetSegmentsAsync(itemId));

        // The delete tombstones the row and clears the item's analysis record so
        // re-analysis can look for other segments.
        await database.DeleteSegmentAsync(itemId, row.Id);

        // Restoring is the undo of that delete, so the record returns under the
        // hash the row carried — otherwise the next scan re-analyzes the item and
        // the pass's replace deletes the row whenever the analyzer no longer emits
        // its exact boundaries.
        var restored = await database.RestoreSegmentAsync(itemId, row.Id);
        Assert.NotNull(restored);
        Assert.Equal(string.Empty, restored!.ConfigHash);

        await using var db = _db.Context();
        var record = Assert.Single(await db.AnalyzedItems.AsNoTracking().ToListAsync());
        Assert.Equal(itemId, record.ItemId);
        Assert.Equal(AnalysisMode.Introduction, record.Type);
        Assert.Equal("hash-1", record.ConfigHash);
    }

    [Fact]
    public async Task RestoreSegment_KeepsANewerAnalysisRecord_AndArmsNothingForAHashlessRow()
    {
        var recordedItemId = Guid.NewGuid();
        var hashlessItemId = Guid.NewGuid();
        var database = _db.Database;

        // Analysis ran again between the delete and the restore: its record is
        // newer state and must not be clobbered with the row's older hash.
        await database.ReplaceAutoSegmentsAsync(recordedItemId, AnalysisMode.Introduction, [new Segment(recordedItemId, new TimeRange(10, 60))], SegmentSource.Chromaprint, "hash-old");
        var recordedRow = Assert.Single(await database.GetSegmentsAsync(recordedItemId));
        await database.DeleteSegmentAsync(recordedItemId, recordedRow.Id);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [recordedItemId], "hash-new");
        Assert.NotNull(await database.RestoreSegmentAsync(recordedItemId, recordedRow.Id));

        // A row that never carried a hash (legacy import) has nothing to re-arm with.
        await database.ReplaceAutoSegmentsAsync(hashlessItemId, AnalysisMode.Credits, [new Segment(hashlessItemId, new TimeRange(100, 160))], SegmentSource.Unknown);
        var hashlessRow = Assert.Single(await database.GetSegmentsAsync(hashlessItemId));
        await database.DeleteSegmentAsync(hashlessItemId, hashlessRow.Id);
        Assert.NotNull(await database.RestoreSegmentAsync(hashlessItemId, hashlessRow.Id));

        await using var db = _db.Context();
        var record = Assert.Single(await db.AnalyzedItems.AsNoTracking().ToListAsync());
        Assert.Equal(recordedItemId, record.ItemId);
        Assert.Equal("hash-new", record.ConfigHash);
    }

    [Fact]
    public async Task TombstonedSegment_NotConvertedToJellyfinDto()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Commercial,
            [new Segment(itemId, new TimeRange(10, 20)), new Segment(itemId, new TimeRange(50, 60))],
            SegmentSource.Chapter);
        var rows = await database.GetSegmentsAsync(itemId);
        await database.DeleteSegmentAsync(itemId, rows[0].Id);

        var factory = new SegmentDtoFactory(database);
        var dtos = await factory.CreateAsync(itemId, default);

        // Only the surviving active row syncs to Jellyfin — carrying its plugin id.
        var dto = Assert.Single(dtos);
        Assert.Equal(rows[1].Id, dto.Id);
        Assert.Equal(rows[1].StartTicks, dto.StartTicks);
    }
}
