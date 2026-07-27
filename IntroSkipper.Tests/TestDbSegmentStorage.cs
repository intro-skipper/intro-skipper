// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestDbSegmentStorage
{
    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    [InlineData(AnalysisMode.Preview)]
    [InlineData(AnalysisMode.Recap)]
    [InlineData(AnalysisMode.Commercial)]
    public void AllowsMultipleSegmentsPerItemAndType_ForEveryMode(AnalysisMode mode)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

        var itemId = Guid.NewGuid();

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();

            db.Segments.AddRange(
                new DbSegment(itemId, mode, TickConversions.FromSeconds(0), TickConversions.FromSeconds(10), SegmentSource.Chapter),
                new DbSegment(itemId, mode, TickConversions.FromSeconds(20), TickConversions.FromSeconds(30), SegmentSource.Chapter),
                new DbSegment(itemId, mode, TickConversions.FromSeconds(40), TickConversions.FromSeconds(50), SegmentSource.User));
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            var count = db.Segments.Count(segment => segment.ItemId == itemId && segment.Type == mode);
            Assert.Equal(3, count);
        }
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    [InlineData(AnalysisMode.Preview)]
    [InlineData(AnalysisMode.Recap)]
    [InlineData(AnalysisMode.Commercial)]
    public void UniqueIndex_RejectsExactDuplicateRange_ForEveryMode(AnalysisMode mode)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

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
    public void UniqueIndex_AllowsSameRangeForDifferentItems()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

        var itemIdA = Guid.NewGuid();
        var itemIdB = Guid.NewGuid();

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();

            db.Segments.AddRange(
                new DbSegment(itemIdA, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(10), SegmentSource.Chapter),
                new DbSegment(itemIdB, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(10), SegmentSource.Chapter));

            // No exception — different items may store the same range.
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            Assert.Equal(1, db.Segments.Count(s => s.ItemId == itemIdA && s.Type == AnalysisMode.Introduction));
            Assert.Equal(1, db.Segments.Count(s => s.ItemId == itemIdB && s.Type == AnalysisMode.Introduction));
        }
    }

    [Fact]
    public void SegmentRow_RoundTripsAllColumns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

        var itemId = Guid.NewGuid();
        Guid segmentId;

        using (var db = new IntroSkipperDbContext(options))
        {
            db.Database.EnsureCreated();

            var row = new DbSegment(itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(10), TickConversions.FromSeconds(60), SegmentSource.User, "hash");
            segmentId = row.Id;
            Assert.NotEqual(Guid.Empty, segmentId);

            db.Segments.Add(row);
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            var segment = db.Segments.Single(s => s.ItemId == itemId && s.Type == AnalysisMode.Introduction);

            Assert.Equal(segmentId, segment.Id);
            Assert.Equal(TickConversions.FromSeconds(10), segment.StartTicks);
            Assert.Equal(TickConversions.FromSeconds(60), segment.EndTicks);
            Assert.Equal(SegmentSource.User, segment.Source);
            Assert.True(segment.IsUserProvided);
            Assert.Equal(SegmentState.Active, segment.State);
            Assert.Equal("hash", segment.ConfigHash);
        }
    }

    [Fact]
    public void SaveChanges_StampsCreatedAndUpdatedTimestamps()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
            .UseSqlite(connection)
            .Options;

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
    public async Task GetStaleTimestampEpisodeIdsAsync_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
    {
        const int LargeEpisodeCount = 33_000;

        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var retainedItemId = Guid.NewGuid();
        var staleItemId = Guid.NewGuid();
        var enabledEpisodeIds = Enumerable.Range(0, LargeEpisodeCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(retainedItemId)
            .ToHashSet();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.ApplyMigrationsAsync();
                db.Segments.AddRange(
                    new DbSegment(retainedItemId, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(10), SegmentSource.Chapter),
                    new DbSegment(staleItemId, AnalysisMode.Introduction, TickConversions.FromSeconds(20), TickConversions.FromSeconds(30), SegmentSource.Chapter));
                await db.SaveChangesAsync();
            }

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var staleEpisodeIds = await database.GetStaleTimestampEpisodeIdsAsync(enabledEpisodeIds);

            Assert.Equal([staleItemId], staleEpisodeIds);

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.Equal(2, db.Segments.Count());
            }

            await database.DeleteSegmentsForItemsAsync(staleEpisodeIds);

            using var cleanedDb = new IntroSkipperDbContext(dbPath);
            var itemId = Assert.Single(cleanedDb.Segments.Select(segment => segment.ItemId));
            Assert.Equal(retainedItemId, itemId);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetSeasonQueueSnapshotAsync_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
    {
        const int LargeEpisodeCount = 1_001;

        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var episodeWithSegmentId = Guid.NewGuid();
        var episodeIds = Enumerable.Range(0, LargeEpisodeCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(episodeWithSegmentId)
            .ToList();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.ApplyMigrationsAsync();
                db.Segments.Add(new DbSegment(episodeWithSegmentId, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.Chromaprint));
                db.SeasonStates.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Introduction,
                    AnalyzerAction.Chromaprint,
                    [episodeWithSegmentId],
                    "snapshot-config"));
                await db.SaveChangesAsync();
            }

            // Should not throw even with 1001 episode IDs (above the SQLite 999-parameter limit).
            var snapshot = await DatabaseTestHelpers.CreateSegmentDatabase(dbPath).GetSeasonQueueSnapshotAsync(seasonId, episodeIds);
            Assert.True(snapshot.EpisodeIdsByMode.TryGetValue(AnalysisMode.Introduction, out var analyzedIds));
            Assert.Contains(episodeWithSegmentId, analyzedIds!);
            Assert.True(snapshot.ConfigHashByMode.TryGetValue(AnalysisMode.Introduction, out var configHash));
            Assert.Equal("snapshot-config", configHash);
            Assert.True(snapshot.AnalyzerActionByMode.TryGetValue(AnalysisMode.Introduction, out var analyzerAction));
            Assert.Equal(AnalyzerAction.Chromaprint, analyzerAction);

            Assert.True(snapshot.SegmentsByEpisodeId.TryGetValue(episodeWithSegmentId, out var segmentsByAnalysisMode));
            Assert.True(segmentsByAnalysisMode!.TryGetValue(AnalysisMode.Introduction, out var introSegments));
            Assert.Single(introSegments!);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetSeasonQueueSnapshot_ReportsAllActiveSegmentsPerMode_AndExcludesSuppressed()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.ApplyMigrationsAsync();
                var suppressed = new DbSegment(episodeId, AnalysisMode.Commercial, TickConversions.FromSeconds(100), TickConversions.FromSeconds(120), SegmentSource.Chapter)
                {
                    State = SegmentState.Suppressed
                };
                db.Segments.AddRange(
                    new DbSegment(episodeId, AnalysisMode.Commercial, TickConversions.FromSeconds(40), TickConversions.FromSeconds(60), SegmentSource.Chapter),
                    new DbSegment(episodeId, AnalysisMode.Commercial, TickConversions.FromSeconds(10), TickConversions.FromSeconds(30), SegmentSource.User),
                    suppressed);
                await db.SaveChangesAsync();
            }

            var snapshot = await DatabaseTestHelpers.CreateSegmentDatabase(dbPath).GetSeasonQueueSnapshotAsync(seasonId, [episodeId]);

            var commercials = snapshot.SegmentsByEpisodeId[episodeId][AnalysisMode.Commercial];
            Assert.Equal(2, commercials.Count);

            // Ordered by start; the suppressed row is invisible.
            Assert.Equal(10, commercials[0].Start);
            Assert.Equal(40, commercials[1].Start);

            Assert.Contains(episodeId, snapshot.UserProvidedByMode[AnalysisMode.Commercial]);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ApplyMigrations_CreatesCurrentSchemaFromEmptyDatabase()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.ApplyMigrationsAsync();

                var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
                Assert.Empty(pendingMigrations);

                // The whole schema comes from ONE baseline migration.
                var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
                Assert.Single(appliedMigrations);

                db.SeasonStates.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Introduction,
                    AnalyzerAction.Default,
                    [episodeId],
                    "season-config"));
                db.Segments.Add(new DbSegment(episodeId, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.Chapter, "segment-config"));
                await db.SaveChangesAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var seasonState = await db.SeasonStates.SingleAsync();
                var segment = await db.Segments.SingleAsync();

                Assert.Equal("season-config", seasonState.ConfigHash);
                Assert.Empty(seasonState.SettledReanalysisEpisodeIds);
                Assert.Equal(new[] { episodeId }, seasonState.EpisodeIds);
                Assert.Equal("segment-config", segment.ConfigHash);
                Assert.NotEqual(Guid.Empty, segment.Id);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Theory]
    [InlineData(60.0, 1440.0, false, 0)]   // overlapping, auto-detected → rejected
    [InlineData(1200.0, 1440.0, false, 1)] // non-overlapping, auto-detected → accepted
    [InlineData(60.0, 1440.0, true, 1)]    // overlapping, user-provided → accepted
    public async Task ReplaceAutoSegmentsAsync_CreditsOverlapGuard(
        double creditsStart, double creditsEnd, bool isUserProvided, int expectedCount)
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var itemId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.ApplyMigrationsAsync();
                // Store an intro: 0–90 s.
                db.Segments.Add(new DbSegment(itemId, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(90), SegmentSource.Chromaprint));
                await db.SaveChangesAsync();
            }

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            if (isUserProvided)
            {
                await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, TickConversions.FromSeconds(creditsStart), TickConversions.FromSeconds(creditsEnd));
            }
            else
            {
                var credits = new Segment(itemId, new TimeRange(creditsStart, creditsEnd));
                await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Credits, [credits], SegmentSource.BlackFrame);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var count = db.Segments.Count(s => s.ItemId == itemId && s.Type == AnalysisMode.Credits);
                Assert.Equal(expectedCount, count);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    private static void DeleteSqliteFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
