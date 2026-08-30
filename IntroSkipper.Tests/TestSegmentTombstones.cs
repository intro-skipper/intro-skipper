// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
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
/// remembered and never re-added by re-analysis (GitHub issue #863), survives
/// config-hash cleanup, and is cleared by explicit erase operations.
/// </summary>
public sealed class TestSegmentTombstones
{
    [Fact]
    public async Task DeleteAutoSegment_Tombstones_AndHidesFromReads()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
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
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteUserSegment_HardDeletes_NoTombstoneRemains()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var row = await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));

            var snapshot = await database.DeleteSegmentAsync(itemId, row.Id);

            Assert.NotNull(snapshot);
            Assert.Equal(SegmentSource.User, snapshot!.Source);
            Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Theory]
    [InlineData(10.0, 60.0)]   // identical range
    [InlineData(12.0, 63.0)]   // shifted re-detection, still overlapping
    [InlineData(59.0, 100.0)]  // partial overlap at the tail
    public async Task AnalysisWrite_OverlappingTombstonedRange_IsSkipped(double newStart, double newEnd)
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);
            var row = Assert.Single(await database.GetSegmentsAsync(itemId));
            await database.DeleteSegmentAsync(itemId, row.Id);

            // Re-analysis re-derives an overlapping range: it must not resurrect the
            // segment the user deleted, and must not throw on the unique index.
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(newStart, newEnd))], SegmentSource.Chromaprint);

            Assert.Empty(await database.GetSegmentsAsync(itemId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Theory]
    [InlineData(100.0, 120.0)] // disjoint range elsewhere in the episode
    [InlineData(20.0, 30.0)]   // starts exactly at the tombstone's end — touching is not overlap
    [InlineData(0.0, 10.0)]    // ends exactly at the tombstone's start — touching is not overlap
    public async Task AnalysisWrite_DifferentRange_InsertsDespiteTombstone(double newStart, double newEnd)
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Commercial, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
            var row = Assert.Single(await database.GetSegmentsAsync(itemId));
            await database.DeleteSegmentAsync(itemId, row.Id);

            // A commercial that does not strictly overlap the deleted one — including one
            // that exactly abuts it — is unrelated and must be stored.
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Commercial, [new Segment(itemId, new TimeRange(newStart, newEnd))], SegmentSource.Chapter);

            var active = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.Equal(Ticks(newStart), active.StartTicks);
            Assert.Equal(Ticks(newEnd), active.EndTicks);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task AnalysisWrite_FullyRejected_LeavesStandingRowsUntouched()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
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
                [new Segment(itemId, new TimeRange(105, 145))],
                SegmentSource.Chromaprint);

            Assert.Equal(0, stored);
            var survivor = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.Equal(Ticks(10), survivor.StartTicks);
            Assert.Equal(Ticks(60), survivor.EndTicks);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UpdateSegmentAsync_ReclaimsTombstonedRange_ByAbsorbingTombstone()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
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
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RestoreSegmentAsync_ReactivatesWithOriginalSource()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
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
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RestoreSegmentAsync_ReArmsClearedAnalysisRecordWithTheRowsHash()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint, "hash-1");
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [itemId], "hash-1");
            var row = Assert.Single(await database.GetSegmentsAsync(itemId));

            // The editor's delete cascade tombstones the row and clears the item's
            // analysis record so re-analysis can look for other segments.
            await database.DeleteSegmentAsync(itemId, row.Id);
            await database.ClearItemAnalysisAsync(itemId, AnalysisMode.Introduction);

            // Restoring is the undo of that delete, so the record returns under the
            // hash the row carried — otherwise the next scan re-analyzes the item and
            // the pass's replace deletes the row whenever the analyzer no longer emits
            // its exact boundaries.
            var restored = await database.RestoreSegmentAsync(itemId, row.Id);
            Assert.NotNull(restored);
            Assert.Equal(string.Empty, restored!.ConfigHash);

            await using var db = new IntroSkipperDbContext(dbPath);
            var record = Assert.Single(await db.AnalyzedItems.AsNoTracking().ToListAsync());
            Assert.Equal(itemId, record.ItemId);
            Assert.Equal(AnalysisMode.Introduction, record.Type);
            Assert.Equal("hash-1", record.ConfigHash);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RestoreSegmentAsync_KeepsANewerAnalysisRecord_AndArmsNothingForAHashlessRow()
    {
        var dbPath = CreateTempDbPath();
        var recordedItemId = Guid.NewGuid();
        var hashlessItemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

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

            await using var db = new IntroSkipperDbContext(dbPath);
            var record = Assert.Single(await db.AnalyzedItems.AsNoTracking().ToListAsync());
            Assert.Equal(recordedItemId, record.ItemId);
            Assert.Equal("hash-new", record.ConfigHash);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UndoDeleteAsync_ReversesBothDeleteShapes()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chapter, "cfg");
            var autoRow = Assert.Single(await database.GetSegmentsAsync(itemId));
            var userRow = await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));

            // Tombstoned auto row: undo flips the tombstone back.
            var autoDelete = await database.DeleteSegmentAsync(itemId, autoRow.Id);
            await database.UndoDeleteAsync(autoDelete);
            var restored = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.Id == autoRow.Id);
            Assert.Equal(SegmentState.Active, restored.State);
            Assert.Equal("cfg", restored.ConfigHash);

            // Hard-deleted user row: undo re-inserts the snapshot verbatim.
            var userDelete = await database.DeleteSegmentAsync(itemId, userRow.Id);
            await database.UndoDeleteAsync(userDelete);
            var reinserted = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.Id == userRow.Id);
            Assert.Equal(SegmentSource.User, reinserted.Source);
            Assert.Equal(userRow.CreatedAt, reinserted.CreatedAt);

            // Nothing deleted → no-op.
            await database.UndoDeleteAsync(null);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UndoDeleteAsync_SwallowsReinsert_WhenEquivalentRowAppeared()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var userRow = await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));
            var snapshot = await database.DeleteSegmentAsync(itemId, userRow.Id);

            // An equivalent row (same range, new id) appears before the undo runs: the
            // re-insert hits the unique quadruple index and is swallowed, so the
            // occupant survives and the rollback path does not fail.
            var occupant = await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));
            await database.UndoDeleteAsync(snapshot);

            var row = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.Equal(occupant.Id, row.Id);
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ExplicitErase_ClearsTombstones_SoReanalysisCanReAdd()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);
            var row = Assert.Single(await database.GetSegmentsAsync(itemId));
            await database.DeleteSegmentAsync(itemId, row.Id);

            // Erase-by-mode is a factory reset: the tombstone goes too, and the next
            // analysis run may store the range again.
            await database.DeleteSegmentsByModeAsync(AnalysisMode.Introduction);
            Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));

            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);
            Assert.Single(await database.GetSegmentsAsync(itemId));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task TombstonedSegment_NotConvertedToJellyfinDto()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
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
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    private static string CreateTempDbPath()
        => DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-tombstones.db");
}
