// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestDbSegmentStorage
{
    [Fact]
    public void AllowsMultipleCommercialSegments()
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

            var firstSegment = new DbSegment(
                new Segment(itemId, new TimeRange(0, 10)),
                AnalysisMode.Commercial);
            var secondSegment = new DbSegment(
                new Segment(itemId, new TimeRange(20, 30)),
                AnalysisMode.Commercial);

            db.DbSegment.AddRange(firstSegment, secondSegment);
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            var count = db.DbSegment.Count(segment => segment.ItemId == itemId && segment.Type == AnalysisMode.Commercial);
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public void NonCommercialUniqueIndexPreventsInsertingDuplicateForSameItemAndType()
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

            db.DbSegment.Add(new DbSegment(
                new Segment(itemId, new TimeRange(0, 10)),
                AnalysisMode.Introduction));
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            // Attempting to insert a second Introduction segment for the same item must
            // violate the non-commercial unique index and throw a DbUpdateException.
            db.DbSegment.Add(new DbSegment(
                new Segment(itemId, new TimeRange(5, 15)),
                AnalysisMode.Introduction));

            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }

    [Fact]
    public void NonCommercialUniqueIndexAllowsSameModeForDifferentItems()
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

            db.DbSegment.AddRange(
                new DbSegment(new Segment(itemIdA, new TimeRange(0, 10)), AnalysisMode.Introduction),
                new DbSegment(new Segment(itemIdB, new TimeRange(0, 10)), AnalysisMode.Introduction));

            // No exception — different items may have the same mode.
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            Assert.Equal(1, db.DbSegment.Count(s => s.ItemId == itemIdA && s.Type == AnalysisMode.Introduction));
            Assert.Equal(1, db.DbSegment.Count(s => s.ItemId == itemIdB && s.Type == AnalysisMode.Introduction));
        }
    }

    [Fact]
    public void UserProvidedFlagIsPreservedOnDbSegment()
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

            db.DbSegment.Add(new DbSegment(
                new Segment(itemId, new TimeRange(10, 60)),
                AnalysisMode.Introduction,
                isUserProvided: true));
            db.SaveChanges();
        }

        using (var db = new IntroSkipperDbContext(options))
        {
            var segment = db.DbSegment
                .Single(s => s.ItemId == itemId && s.Type == AnalysisMode.Introduction);

            Assert.True(segment.IsUserProvided);
        }
    }

    [Fact]
    public async Task CleanTimestampsAsync_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
    {
        const int LargeEpisodeCount = 33_000;

        var tempDir = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests");
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
                await db.Database.EnsureCreatedAsync();
                db.DbSegment.AddRange(
                    new DbSegment(new Segment(retainedItemId, new TimeRange(0, 10)), AnalysisMode.Introduction),
                    new DbSegment(new Segment(staleItemId, new TimeRange(20, 30)), AnalysisMode.Introduction));
                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                await plugin.CleanTimestampsAsync(enabledEpisodeIds);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var itemId = Assert.Single(db.DbSegment.Select(segment => segment.ItemId));
                Assert.Equal(retainedItemId, itemId);
            }
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

        var tempDir = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests");
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
                await db.Database.EnsureCreatedAsync();
                db.DbSegment.Add(new DbSegment(
                    new Segment(episodeWithSegmentId, new TimeRange(0, 30)),
                    AnalysisMode.Introduction));
                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                // Should not throw even with 1001 episode IDs (above the SQLite 999-parameter limit).
                var snapshot = await plugin.GetSeasonQueueSnapshotAsync(seasonId, episodeIds);

                Assert.True(snapshot.SegmentsByEpisodeId.TryGetValue(episodeWithSegmentId, out var segmentsByAnalysisMode));
                Assert.True(segmentsByAnalysisMode!.TryGetValue(AnalysisMode.Introduction, out _));
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
    public async Task UpdateTimestampAsync_CreditsOverlapGuard(
        double creditsStart, double creditsEnd, bool isUserProvided, int expectedCount)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var itemId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                // Store an intro: 0–90 s.
                db.DbSegment.Add(new DbSegment(
                    new Segment(itemId, new TimeRange(0, 90)),
                    AnalysisMode.Introduction));
                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);
                ConfigurePluginLogger(plugin);

                var credits = new Segment(itemId, new TimeRange(creditsStart, creditsEnd));
                await plugin.UpdateTimestampAsync(credits, AnalysisMode.Credits, isUserProvided);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var count = db.DbSegment.Count(s => s.ItemId == itemId && s.Type == AnalysisMode.Credits);
                Assert.Equal(expectedCount, count);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    private static void ConfigurePluginLogger(Plugin plugin)
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        EntrypointTestHelpers.SetPrivateField(plugin, "_logger", loggerFactory.CreateLogger<Plugin>());
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
