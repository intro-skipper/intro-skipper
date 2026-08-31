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
        var options = CreateInMemoryOptions(connection);

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
    public void UniqueIndex_AllowsSameRangeForDifferentItems()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = CreateInMemoryOptions(connection);

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
        var options = CreateInMemoryOptions(connection);

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
            Assert.Equal(SegmentState.Active, segment.State);
            Assert.Equal("hash", segment.ConfigHash);
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

            await database.EraseItemsAsync(staleEpisodeIds);

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
                db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Chromaprint));
                db.AnalyzedItems.Add(new DbAnalyzedItem(episodeWithSegmentId, AnalysisMode.Introduction, "snapshot-config"));
                await db.SaveChangesAsync();
            }

            // Should not throw even with 1001 episode IDs (above the SQLite 999-parameter limit).
            var snapshot = await DatabaseTestHelpers.CreateSegmentDatabase(dbPath).GetSeasonQueueSnapshotAsync(seasonId, episodeIds);
            Assert.Equal("snapshot-config", snapshot.AnalyzedConfigHashes[(episodeWithSegmentId, AnalysisMode.Introduction)]);
            Assert.True(snapshot.AnalyzerActionByMode.TryGetValue(AnalysisMode.Introduction, out var analyzerAction));
            Assert.Equal(AnalyzerAction.Chromaprint, analyzerAction);

            Assert.True(snapshot.SegmentModesByEpisodeId.TryGetValue(episodeWithSegmentId, out var modesWithSegments));
            Assert.Contains(AnalysisMode.Introduction, modesWithSegments!);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetSeasonQueueSnapshot_ReportsModesWithActiveSegments_AndExcludesSuppressed()
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

            var snapshot = await DatabaseTestHelpers.CreateSegmentDatabase(dbPath).GetSeasonQueueSnapshotAsync(seasonId, [episodeId]);

            // A mode whose only rows are tombstones has no active segment and must not be reported.
            var modes = snapshot.SegmentModesByEpisodeId[episodeId];
            Assert.Contains(AnalysisMode.Commercial, modes);
            Assert.DoesNotContain(AnalysisMode.Preview, modes);

            Assert.Contains(episodeId, snapshot.UserProvidedByMode[AnalysisMode.Commercial]);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task Segments_RejectDegenerateRange_AtTheDatabase()
    {
        var dbPath = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", Guid.NewGuid().ToString("N") + ".db");

        try
        {
            using var db = new IntroSkipperDbContext(dbPath);
            await db.ApplyMigrationsAsync();

            // Every facade write validates the range; the CHECK constraint is the backstop
            // for paths that do not (raw SQL, a future bug), so a degenerate row can never
            // reach the Jellyfin mirror.
            db.Segments.Add(new DbSegment(Guid.NewGuid(), AnalysisMode.Introduction, TickConversions.FromSeconds(10), TickConversions.FromSeconds(10), SegmentSource.Chapter));

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.IsType<SqliteException>(exception.InnerException);
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

                // The whole v2 schema comes from one baseline migration; anything added
                // after the first release lands as a plain EF migration on top.
                string[] appliedMigrations = [.. await db.Database.GetAppliedMigrationsAsync()];
                Assert.Equal(2, appliedMigrations.Length);
                Assert.EndsWith("_InitialCreate", appliedMigrations[0], StringComparison.Ordinal);
                Assert.EndsWith("_AddSegmentProjectionJournal", appliedMigrations[1], StringComparison.Ordinal);

                db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default));
                db.AnalyzedItems.Add(new DbAnalyzedItem(episodeId, AnalysisMode.Introduction, "season-config"));
                db.Segments.Add(new DbSegment(episodeId, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.Chapter, "segment-config"));
                await db.SaveChangesAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
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
