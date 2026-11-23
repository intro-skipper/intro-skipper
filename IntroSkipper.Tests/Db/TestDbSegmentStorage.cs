// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Schema-level invariants of <c>introskipper-v2.db</c>: the range CHECK constraint,
/// timestamp stamping, and the migration baseline.
/// </summary>
public sealed class TestDbSegmentStorage : IDisposable
{
    private readonly TempSegmentDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SaveChanges_StampsCreatedAndUpdatedTimestamps()
    {
        var itemId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        await using (var db = _db.Context())
        {
            await db.Database.MigrateAsync();
            db.Segments.Add(new DbSegment(itemId, AnalysisMode.Introduction, 0, 100, SegmentSource.Chapter));
            await db.SaveChangesAsync();
        }

        var afterInsert = DateTime.UtcNow;
        DateTime createdAt;

        await using (var db = _db.Context())
        {
            var segment = await db.Segments.SingleAsync(s => s.ItemId == itemId);
            createdAt = segment.CreatedAt;

            Assert.InRange(segment.CreatedAt, before, afterInsert);
            Assert.InRange(segment.UpdatedAt, before, afterInsert);
            Assert.Equal(DateTimeKind.Utc, segment.CreatedAt.Kind);

            // Modifying the row refreshes UpdatedAt but keeps CreatedAt.
            segment.State = SegmentState.Suppressed;
            await db.SaveChangesAsync();
        }

        await using (var db = _db.Context())
        {
            var segment = await db.Segments.SingleAsync(s => s.ItemId == itemId);
            Assert.Equal(createdAt, segment.CreatedAt);
            Assert.True(segment.UpdatedAt >= segment.CreatedAt);
        }
    }

    [Fact]
    public async Task GetSeasonQueueSnapshot_ReportsModesWithActiveSegments_AndExcludesSuppressed()
    {
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        using (var db = _db.Context())
        {
            await db.Database.MigrateAsync();
            var suppressed = new DbSegment(episodeId, AnalysisMode.Preview, TickConversions.FromSeconds(100), TickConversions.FromSeconds(120), SegmentSource.Chapter)
            {
                State = SegmentState.Suppressed
            };
            db.Segments.AddRange(
                new DbSegment(episodeId, AnalysisMode.Commercial, TickConversions.FromSeconds(40), TickConversions.FromSeconds(60), SegmentSource.Chapter),
                new DbSegment(episodeId, AnalysisMode.Commercial, TickConversions.FromSeconds(10), TickConversions.FromSeconds(30), SegmentSource.User),
                suppressed);
            await db.SaveChangesAsync();
        }

        var snapshot = await _db.Database.GetSeasonQueueSnapshotAsync(seasonId, [episodeId]);

        // A mode whose only rows are tombstones has no active segment and must not be reported.
        var modes = snapshot.SegmentModesByEpisodeId[episodeId];
        Assert.Contains(AnalysisMode.Commercial, modes);
        Assert.DoesNotContain(AnalysisMode.Preview, modes);

        Assert.Contains(episodeId, snapshot.UserProvidedByMode[AnalysisMode.Commercial]);
    }

    [Fact]
    public async Task Segments_RejectDegenerateRange_AtTheDatabase()
    {
        using var db = _db.Context();
        await db.Database.MigrateAsync();

        // Every facade write validates the range; the CHECK constraint is the backstop
        // for paths that do not (raw SQL, a future bug), so a degenerate row can never
        // reach the Jellyfin mirror.
        db.Segments.Add(new DbSegment(Guid.NewGuid(), AnalysisMode.Introduction, TickConversions.FromSeconds(10), TickConversions.FromSeconds(10), SegmentSource.Chapter));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<SqliteException>(exception.InnerException);
    }

    [Fact]
    public async Task ApplyMigrations_CreatesCurrentSchemaFromEmptyDatabase()
    {
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        using (var db = _db.Context())
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default));
            db.AnalyzedItems.Add(new DbAnalyzedItem(episodeId, AnalysisMode.Introduction, "season-config"));
            db.Segments.Add(new DbSegment(episodeId, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.Chapter, "segment-config"));
            await db.SaveChangesAsync();
        }

        using (var db = _db.Context())
        {
            var seasonState = await db.SeasonStates.SingleAsync();
            var analyzed = await db.AnalyzedItems.SingleAsync();
            var segment = await db.Segments.SingleAsync();

            Assert.Empty(seasonState.SettledReanalysisEpisodeIds);
            Assert.Equal(episodeId, analyzed.ItemId);
            Assert.Equal("season-config", analyzed.ConfigHash);
            Assert.Equal("segment-config", segment.ConfigHash);
            Assert.NotEqual(Guid.Empty, segment.Id);
        }
    }
}
