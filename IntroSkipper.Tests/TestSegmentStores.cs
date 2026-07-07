// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Tests for the store layer (Theory A prototype): domain invariants enforced by
/// <see cref="IntroSkipper.Services.SegmentUpdateService"/> over <see cref="SegmentStore"/>,
/// the unchunked single-JSON-parameter delete path, and the initialization gate ordering.
/// </summary>
public sealed class TestSegmentStores
{
    [Fact]
    public async Task SegmentUpdateService_DoesNotOverwriteUserProvidedSegment_WithAnalysisResult()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();

        try
        {
            await EnsureSchemaAsync(dbPath);
            var service = EntrypointTestHelpers.CreateSegmentUpdateService(dbPath);

            // User provides an intro via the segment editor.
            await service.UpdateTimestampAsync(
                new Segment(itemId, new TimeRange(10, 60)),
                AnalysisMode.Introduction,
                isUserProvided: true);

            // A later analysis run produces a different intro; it must be discarded.
            await service.UpdateTimestampAsync(
                new Segment(itemId, new TimeRange(100, 150)),
                AnalysisMode.Introduction,
                isUserProvided: false);

            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                var stored = await db.DbSegment.SingleAsync(s => s.ItemId == itemId && s.Type == AnalysisMode.Introduction);
                Assert.True(stored.IsUserProvided);
                Assert.Equal(10, stored.Start);
                Assert.Equal(60, stored.End);
            }

            // A user-provided update replaces the user-provided segment.
            await service.UpdateTimestampAsync(
                new Segment(itemId, new TimeRange(20, 70)),
                AnalysisMode.Introduction,
                isUserProvided: true);

            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                var stored = await db.DbSegment.SingleAsync(s => s.ItemId == itemId && s.Type == AnalysisMode.Introduction);
                Assert.True(stored.IsUserProvided);
                Assert.Equal(20, stored.Start);
                Assert.Equal(70, stored.End);
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
    public async Task SegmentUpdateService_CreditsOverlapGuard(
        double creditsStart, double creditsEnd, bool isUserProvided, int expectedCount)
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();

        try
        {
            await EnsureSchemaAsync(dbPath);
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                // Store an intro: 0–90 s.
                db.DbSegment.Add(new DbSegment(
                    new Segment(itemId, new TimeRange(0, 90)),
                    AnalysisMode.Introduction));
                await db.SaveChangesAsync();
            }

            var service = EntrypointTestHelpers.CreateSegmentUpdateService(dbPath);
            await service.UpdateTimestampAsync(
                new Segment(itemId, new TimeRange(creditsStart, creditsEnd)),
                AnalysisMode.Credits,
                isUserProvided);

            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                var count = await db.DbSegment.CountAsync(s => s.ItemId == itemId && s.Type == AnalysisMode.Credits);
                Assert.Equal(expectedCount, count);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task SegmentUpdateService_DeduplicatesCommercialSegments_WithinEpsilon()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();

        try
        {
            await EnsureSchemaAsync(dbPath);
            var service = EntrypointTestHelpers.CreateSegmentUpdateService(dbPath);

            await service.UpdateTimestampAsync(new Segment(itemId, new TimeRange(0, 30)), AnalysisMode.Commercial);
            await service.UpdateTimestampAsync(new Segment(itemId, new TimeRange(0.0005, 30.0005)), AnalysisMode.Commercial); // duplicate within epsilon
            await service.UpdateTimestampAsync(new Segment(itemId, new TimeRange(600, 660)), AnalysisMode.Commercial); // distinct commercial

            await using var db = new IntroSkipperDbContext(dbPath);
            var count = await db.DbSegment.CountAsync(s => s.ItemId == itemId && s.Type == AnalysisMode.Commercial);
            Assert.Equal(2, count);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task SegmentStore_CleanTimestampsAsync_HandlesCollectionsAboveSqliteVariableLimit_WithoutChunking()
    {
        // 33 000 > SQLITE_MAX_VARIABLE_NUMBER (32 766): proves the EF.Parameter single-JSON-parameter
        // translation replaces the manual Chunk(500) batching safely.
        const int LargeEpisodeCount = 33_000;

        var dbPath = CreateTempDbPath();
        var retainedItemId = Guid.NewGuid();
        var staleItemId = Guid.NewGuid();
        var enabledEpisodeIds = Enumerable.Range(0, LargeEpisodeCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(retainedItemId)
            .ToHashSet();

        try
        {
            await EnsureSchemaAsync(dbPath);
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                db.DbSegment.AddRange(
                    new DbSegment(new Segment(retainedItemId, new TimeRange(0, 10)), AnalysisMode.Introduction),
                    new DbSegment(new Segment(staleItemId, new TimeRange(20, 30)), AnalysisMode.Introduction));
                await db.SaveChangesAsync();
            }

            var store = EntrypointTestHelpers.CreateSegmentStore(dbPath);
            await store.CleanTimestampsAsync(enabledEpisodeIds);

            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                var itemId = Assert.Single(await db.DbSegment.Select(segment => segment.ItemId).ToListAsync());
                Assert.Equal(retainedItemId, itemId);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DatabaseInitializer_GatesStoreAccess_UntilMigrationsComplete()
    {
        // No schema is created up front: the store's first operation must trigger the initializer,
        // which runs legacy repair + migrations before the query executes.
        var dbPath = CreateTempDbPath();
        var cacheDbPath = dbPath + "-cache.db";
        var itemId = Guid.NewGuid();

        try
        {
            var initializer = EntrypointTestHelpers.CreateDatabaseInitializer(dbPath, cacheDbPath);
            var store = new SegmentStore(new SegmentDbContextFactory(dbPath), initializer);
            var service = new IntroSkipper.Services.SegmentUpdateService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            // Simulate racing consumers on a cold database: all must succeed, initialization runs once.
            var tasks = Enumerable.Range(0, 8)
                .Select(_ => store.GetSegmentsAsync(itemId))
                .ToArray();
            await Task.WhenAll(tasks);
            Assert.All(tasks, t => Assert.Empty(t.Result));

            await service.UpdateTimestampAsync(new Segment(itemId, new TimeRange(5, 25)), AnalysisMode.Introduction);
            var segments = await store.GetSegmentsAsync(itemId);
            Assert.Single(segments);

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public async Task DetectionCacheStore_DeleteForItemsAsync_HandlesCollectionsAboveSqliteVariableLimit()
    {
        const int LargeIdCount = 33_000;

        var cacheDbPath = CreateTempDbPath();
        var deletedItemId = Guid.NewGuid();
        var retainedItemId = Guid.NewGuid();

        try
        {
            var factory = new DetectionCacheDbContextFactory(cacheDbPath);
            using (var db = factory.CreateDbContext())
            {
                db.EnsureSchema();
                db.DetectionCache.AddRange(
                    new DbDetectionCache(deletedItemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, [1, 2], 0, 10, string.Empty),
                    new DbDetectionCache(retainedItemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, [3, 4], 0, 10, string.Empty));
                await db.SaveChangesAsync();
            }

            var store = new DetectionCacheStore(factory);
            var idsToDelete = Enumerable.Range(0, LargeIdCount - 1)
                .Select(_ => Guid.NewGuid())
                .Append(deletedItemId)
                .ToArray();

            var deleted = await store.DeleteForItemsAsync(idsToDelete);
            Assert.Equal(1, deleted);

            using var verifyDb = factory.CreateDbContext();
            var remaining = Assert.Single(await verifyDb.DetectionCache.Select(e => e.ItemId).ToListAsync());
            Assert.Equal(retainedItemId, remaining);
        }
        finally
        {
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public async Task DatabaseInitializer_GatesNeverThrow_WhenInitializationFails()
    {
        // The cache gate runs inside IHostedService.StartAsync; an escaped exception would abort
        // Jellyfin host startup, and the Lazy gate would cache the fault forever. Use a factory that
        // throws a type outside the old (IOException or SqliteException) filter to prove the
        // catch-all containment.
        var dbPath = CreateTempDbPath();

        try
        {
            var initializer = new DatabaseInitializer(
                new SegmentDbContextFactory(dbPath),
                new ThrowingCacheDbContextFactory(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);

            // Must not throw, and repeated calls must not rethrow a cached fault.
            initializer.EnsureCacheDbReady();
            initializer.EnsureCacheDbReady();

            // The hosted warm-up must complete without faulting host startup.
            var startupService = new IntroSkipper.Services.DatabaseStartupService(initializer);
            await startupService.StartAsync(CancellationToken.None);
            await startupService.StopAsync(CancellationToken.None);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DatabaseInitializer_EnforcesWalJournalMode_OnBothDatabases()
    {
        var dbPath = CreateTempDbPath();
        var cacheDbPath = dbPath + "-cache.db";

        try
        {
            var initializer = EntrypointTestHelpers.CreateDatabaseInitializer(dbPath, cacheDbPath);
            await initializer.EnsureSegmentDbReadyAsync();
            initializer.EnsureCacheDbReady();

            Assert.Equal("wal", await ReadJournalModeAsync(dbPath));
            Assert.Equal("wal", await ReadJournalModeAsync(cacheDbPath));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    private static async Task<string?> ReadJournalModeAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private sealed class ThrowingCacheDbContextFactory : IDbContextFactory<DetectionCacheDbContext>
    {
        public DetectionCacheDbContext CreateDbContext()
            => throw new InvalidOperationException("Simulated cache database initialization failure.");
    }

    private static async Task EnsureSchemaAsync(string dbPath)
    {
        await using var db = new IntroSkipperDbContext(dbPath);
        await db.Database.EnsureCreatedAsync();
    }

    private static string CreateTempDbPath()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "stores");
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
