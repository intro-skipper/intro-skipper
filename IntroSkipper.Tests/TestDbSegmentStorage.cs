// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Schema-level invariants of <c>introskipper-v2.db</c>: the unique range index, the
/// range CHECK constraint, timestamp stamping, and the migration baseline.
/// </summary>
public sealed class TestDbSegmentStorage : IDisposable
{
    private readonly string _dbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-storage.db");

    public void Dispose() => DatabaseTestHelpers.DeleteSqliteFiles(_dbPath);

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    [InlineData(AnalysisMode.Preview)]
    [InlineData(AnalysisMode.Recap)]
    [InlineData(AnalysisMode.Commercial)]
    public void UniqueIndex_RejectsExactDuplicateRange_ForEveryMode(AnalysisMode mode)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = CreateInMemoryOptions(connection);

        var itemId = Guid.NewGuid();

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();

            db.Segments.Add(new DbSegment(itemId, mode, TickConversions.FromSeconds(0), TickConversions.FromSeconds(10), SegmentSource.Chapter));
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            // Inserting the exact same (item, type, start, end) quadruple must violate
            // the uniform unique index and throw a DbUpdateException.
            db.Segments.Add(new DbSegment(itemId, mode, TickConversions.FromSeconds(0), TickConversions.FromSeconds(10), SegmentSource.User));

            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }

    [Fact]
    public void SaveChanges_StampsCreatedAndUpdatedTimestamps()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = CreateInMemoryOptions(connection);

        var itemId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();
            db.Segments.Add(new DbSegment(itemId, AnalysisMode.Introduction, 0, 100, SegmentSource.Chapter));
            db.SaveChanges();
        }

        var afterInsert = DateTime.UtcNow;
        DateTime createdAt;

        using (var db = new IntroSkipperDbContext(options))
        {
            var segment = db.Segments.Single(s => s.ItemId == itemId);
            createdAt = segment.CreatedAt;

            Assert.InRange(segment.CreatedAt, before, afterInsert);
            Assert.InRange(segment.UpdatedAt, before, afterInsert);
            Assert.Equal(DateTimeKind.Utc, segment.CreatedAt.Kind);

            // Modifying the row refreshes UpdatedAt but keeps CreatedAt.
            segment.State = SegmentState.Suppressed;
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            var segment = db.Segments.Single(s => s.ItemId == itemId);
            Assert.Equal(createdAt, segment.CreatedAt);
            Assert.True(segment.UpdatedAt >= segment.CreatedAt);
        }
    }

    [Fact]
    public async Task GetSeasonQueueSnapshot_ReportsModesWithActiveSegments_AndExcludesSuppressed()
    {
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        using (var db = DatabaseTestHelpers.CreateSegmentContext(_dbPath))
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

        var snapshot = await DatabaseTestHelpers.CreateSegmentDatabase(_dbPath).GetSeasonQueueSnapshotAsync(seasonId, [episodeId]);

        // A mode whose only rows are tombstones has no active segment and must not be reported.
        var modes = snapshot.SegmentModesByEpisodeId[episodeId];
        Assert.Contains(AnalysisMode.Commercial, modes);
        Assert.DoesNotContain(AnalysisMode.Preview, modes);

        Assert.Contains(episodeId, snapshot.UserProvidedByMode[AnalysisMode.Commercial]);
    }

    [Fact]
    public async Task Segments_RejectDegenerateRange_AtTheDatabase()
    {
        using var db = DatabaseTestHelpers.CreateSegmentContext(_dbPath);
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

        using (var db = DatabaseTestHelpers.CreateSegmentContext(_dbPath))
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default));
            db.AnalyzedItems.Add(new DbAnalyzedItem(episodeId, AnalysisMode.Introduction, "season-config"));
            db.Segments.Add(new DbSegment(episodeId, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.Chapter, "segment-config"));
            await db.SaveChangesAsync();
        }

        using (var db = DatabaseTestHelpers.CreateSegmentContext(_dbPath))
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

    /// <summary>
    /// Opens the shared in-memory connection (the database lives while it stays open;
    /// the caller owns disposal) and builds context options over it.
    /// </summary>
    /// <param name="connection">Unopened in-memory SQLite connection.</param>
    /// <returns>Context options bound to the connection.</returns>
    private static DbContextOptions<IntroSkipperDbContext> CreateInMemoryOptions(SqliteConnection connection)
    {
        connection.Open();
        return new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;
    }
}
