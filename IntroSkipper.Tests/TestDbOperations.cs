// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Tests for the context-extension operations layer (<see cref="SegmentOperations"/> /
/// <see cref="SeasonStateOperations"/>) and the <see cref="DatabaseInitializer"/> init gate.
/// All tests run against real SQLite files, matching the repository's testing convention.
/// </summary>
public sealed class TestDbOperations
{
    [Fact]
    public async Task UpdateTimestampAsync_DoesNotOverwriteUserProvidedSegment()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                await db.UpdateTimestampAsync(
                    new Segment(itemId, new TimeRange(10, 60)),
                    AnalysisMode.Introduction,
                    isUserProvided: true);
            }

            // An automatic analysis result must not replace the user-provided segment.
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.UpdateTimestampAsync(
                    new Segment(itemId, new TimeRange(20, 80)),
                    AnalysisMode.Introduction,
                    isUserProvided: false);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var segment = await db.DbSegment.SingleAsync(s => s.ItemId == itemId);
                Assert.True(segment.IsUserProvided);
                Assert.Equal(10, segment.Start);
                Assert.Equal(60, segment.End);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UpdateTimestampAsync_UserProvidedReplacesAutomaticSegment()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                await db.UpdateTimestampAsync(
                    new Segment(itemId, new TimeRange(10, 60)),
                    AnalysisMode.Introduction,
                    isUserProvided: false);
                await db.UpdateTimestampAsync(
                    new Segment(itemId, new TimeRange(15, 65)),
                    AnalysisMode.Introduction,
                    isUserProvided: true);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var segment = await db.DbSegment.SingleAsync(s => s.ItemId == itemId);
                Assert.True(segment.IsUserProvided);
                Assert.Equal(15, segment.Start);
                Assert.Equal(65, segment.End);
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
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();

                // Store an intro: 0–90 s.
                await db.UpdateTimestampAsync(
                    new Segment(itemId, new TimeRange(0, 90)),
                    AnalysisMode.Introduction);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.UpdateTimestampAsync(
                    new Segment(itemId, new TimeRange(creditsStart, creditsEnd)),
                    AnalysisMode.Credits,
                    isUserProvided);
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

    [Fact]
    public async Task CleanTimestampsAsync_ChunksDeletes_WhenStaleIdCountExceedsLegacyParameterLimit()
    {
        // 1500 stale items forces multiple 500-ID batches and would exceed the classic
        // 999-parameter SQLite limit if the IDs were sent as one unchunked parameter list.
        const int StaleItemCount = 1_500;

        var dbPath = CreateTempDbPath();
        var retainedItemId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                db.DbSegment.Add(new DbSegment(new Segment(retainedItemId, new TimeRange(0, 10)), AnalysisMode.Introduction));
                for (var i = 0; i < StaleItemCount; i++)
                {
                    db.DbSegment.Add(new DbSegment(new Segment(Guid.NewGuid(), new TimeRange(0, 10)), AnalysisMode.Introduction));
                }

                await db.SaveChangesAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.CleanTimestampsAsync([retainedItemId]);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var itemId = Assert.Single(await db.DbSegment.Select(s => s.ItemId).ToListAsync());
                Assert.Equal(retainedItemId, itemId);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CleanStaleAutomaticSegmentsAsync_PreservesUserProvidedSegments_AcrossChunks()
    {
        // More than one 500-ID chunk, with a user-provided segment and a hash-matching
        // segment sprinkled in; only stale automatic segments may be deleted.
        const int ItemCount = 700;

        var dbPath = CreateTempDbPath();
        var itemIds = Enumerable.Range(0, ItemCount).Select(_ => Guid.NewGuid()).ToArray();
        var userProvidedItemId = itemIds[0];
        var currentHashItemId = itemIds[1];

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                foreach (var itemId in itemIds)
                {
                    var isUserProvided = itemId == userProvidedItemId;
                    var configHash = itemId == currentHashItemId ? "current" : "stale";
                    db.DbSegment.Add(new DbSegment(
                        new Segment(itemId, new TimeRange(0, 10)),
                        AnalysisMode.Introduction,
                        isUserProvided,
                        configHash));
                }

                await db.SaveChangesAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.CleanStaleAutomaticSegmentsAsync(itemIds, AnalysisMode.Introduction, "current");
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var remaining = await db.DbSegment.Select(s => s.ItemId).ToListAsync();
                Assert.Equal(2, remaining.Count);
                Assert.Contains(userProvidedItemId, remaining);
                Assert.Contains(currentHashItemId, remaining);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetSegmentsAsync_CompiledQuery_WorksAcrossPathAndOptionsBasedContexts()
    {
        // GetSegmentsAsync uses EF.CompileAsyncQuery. This guards against the compiled
        // delegate being bound to a single model: both context construction styles used in
        // this repository (string path + OnConfiguring, and explicit DbContextOptions) must
        // be able to execute it within one process.
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                await db.UpdateTimestampAsync(new Segment(itemId, new TimeRange(5, 42)), AnalysisMode.Introduction);
                var viaPathContext = await db.GetSegmentsAsync(itemId);
                Assert.Single(viaPathContext);
            }

            var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            using (var db = new IntroSkipperDbContext(options))
            {
                var viaOptionsContext = await db.GetSegmentsAsync(itemId);
                var segment = Assert.Single(viaOptionsContext);
                Assert.Equal(5, segment.Start);
                Assert.Equal(42, segment.End);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GatedFactory_AppliesMigrationsBeforeHandingOutContexts()
    {
        var dbPath = CreateTempDbPath();
        var cacheDbPath = CreateTempDbPath();

        try
        {
            var initializer = EntrypointTestHelpers.CreateDatabaseInitializer(dbPath, cacheDbPath);
            var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            var factory = new GatedIntroSkipperDbContextFactory(initializer, options);

            // The very first context handed out must already see a fully migrated database.
            var db = await factory.CreateDbContextAsync();
            await using (db.ConfigureAwait(false))
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                await db.UpdateTimestampAsync(new Segment(Guid.NewGuid(), new TimeRange(1, 2)), AnalysisMode.Introduction);
            }

            // Initialization is one-time: a second context creation must succeed unchanged.
            var db2 = await factory.CreateDbContextAsync();
            await using (db2.ConfigureAwait(false))
            {
                Assert.Equal(1, await db2.DbSegment.CountAsync());
            }

            // The cache database was initialized by the same gate.
            using (var cacheDb = new DetectionCacheDbContext(cacheDbPath))
            {
                Assert.False(await cacheDb.DetectionCache.AnyAsync());
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    private static string CreateTempDbPath()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "db-operations");
        Directory.CreateDirectory(tempDir);
        return Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");
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
