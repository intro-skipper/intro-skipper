// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Providers;
using Xunit;

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

            var result = await database.DeleteSegmentAsync(row.Id);

            Assert.True(result.Suppressed);
            Assert.Empty(await database.GetSegmentsAsync(itemId));
            var tombstone = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
            Assert.Equal(SegmentState.Suppressed, tombstone.State);
            Assert.Equal(SegmentSource.Chromaprint, tombstone.Source);

            // Deleting a tombstone again is a no-op.
            var repeat = await database.DeleteSegmentAsync(row.Id);
            Assert.Null(repeat.Deleted);
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

            var result = await database.DeleteSegmentAsync(row.Id);

            Assert.False(result.Suppressed);
            Assert.NotNull(result.Deleted);
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
            await database.DeleteSegmentAsync(row.Id);

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

    [Fact]
    public async Task AnalysisWrite_DifferentRange_InsertsDespiteTombstone()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Commercial, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
            var row = Assert.Single(await database.GetSegmentsAsync(itemId));
            await database.DeleteSegmentAsync(row.Id);

            // A commercial elsewhere in the episode is unrelated to the deleted one.
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Commercial, [new Segment(itemId, new TimeRange(100, 120))], SegmentSource.Chapter);

            var active = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.Equal(Ticks(100), active.StartTicks);
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
            await database.DeleteSegmentAsync(rows[0].Id); // tombstone (10, 20)

            // Moving the other segment onto the tombstoned range is an explicit user
            // decision: the tombstone is absorbed instead of raising a phantom 409.
            var updated = await database.UpdateSegmentAsync(rows[1].Id, Ticks(10), Ticks(20));

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
            await database.DeleteSegmentAsync(row.Id);

            Assert.True(await database.RestoreSegmentAsync(row.Id));
            Assert.False(await database.RestoreSegmentAsync(row.Id)); // not suppressed anymore
            Assert.False(await database.RestoreSegmentAsync(Guid.NewGuid()));

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
            var autoDelete = await database.DeleteSegmentAsync(autoRow.Id);
            await database.UndoDeleteAsync(autoDelete);
            var restored = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.Id == autoRow.Id);
            Assert.Equal(SegmentState.Active, restored.State);
            Assert.Equal("cfg", restored.ConfigHash);

            // Hard-deleted user row: undo re-inserts the snapshot verbatim.
            var userDelete = await database.DeleteSegmentAsync(userRow.Id);
            await database.UndoDeleteAsync(userDelete);
            var reinserted = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.Id == userRow.Id);
            Assert.Equal(SegmentSource.User, reinserted.Source);
            Assert.Equal(userRow.CreatedAt, reinserted.CreatedAt);

            // Nothing deleted → no-op.
            await database.UndoDeleteAsync(new SegmentDeleteResult(null, false));
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
            await database.DeleteSegmentAsync(row.Id);

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
            await database.DeleteSegmentAsync(rows[0].Id);

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

    private static long Ticks(double seconds) => TickConversions.FromSeconds(seconds);

    private static string CreateTempDbPath()
        => DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-tombstones.db");
}
