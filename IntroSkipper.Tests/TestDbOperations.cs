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
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task CleanTimestampsAsync_HandlesEnabledIdSetAboveSqliteParameterLimit()
    {
        // 33,000 IDs > SQLITE_MAX_VARIABLE_NUMBER (32,766). The EF.Parameter (json_each)
        // translation must send the whole set as a single JSON parameter; the default
        // one-scalar-parameter-per-element translation would throw 'too many SQL variables'.
        const int EnabledIdCount = 33_000;

        var dbPath = CreateTempDbPath();
        var retainedItemId = Guid.NewGuid();
        var staleItemId = Guid.NewGuid();
        var enabledEpisodeIds = Enumerable.Range(0, EnabledIdCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(retainedItemId)
            .ToArray();

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

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.CleanTimestampsAsync(enabledEpisodeIds);
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
    public async Task CleanStaleAutomaticSegmentsAsync_PreservesUserProvidedSegments_AboveSqliteParameterLimit()
    {
        // 33,000 item IDs (> 32,766) in one call; a user-provided segment and a hash-matching
        // segment must survive, only the stale automatic segment may be deleted.
        const int ItemIdCount = 33_000;

        var dbPath = CreateTempDbPath();
        var userProvidedItemId = Guid.NewGuid();
        var currentHashItemId = Guid.NewGuid();
        var staleItemId = Guid.NewGuid();
        var itemIds = Enumerable.Range(0, ItemIdCount - 3)
            .Select(_ => Guid.NewGuid())
            .Append(userProvidedItemId)
            .Append(currentHashItemId)
            .Append(staleItemId)
            .ToArray();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                db.DbSegment.AddRange(
                    new DbSegment(new Segment(userProvidedItemId, new TimeRange(0, 10)), AnalysisMode.Introduction, isUserProvided: true, configHash: "stale"),
                    new DbSegment(new Segment(currentHashItemId, new TimeRange(0, 10)), AnalysisMode.Introduction, isUserProvided: false, configHash: "current"),
                    new DbSegment(new Segment(staleItemId, new TimeRange(0, 10)), AnalysisMode.Introduction, isUserProvided: false, configHash: "stale"));
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
    public async Task ResetSeasonForReanalysisAsync_HandlesEpisodeIdSetAboveSqliteParameterLimit()
    {
        const int EpisodeIdCount = 33_000;

        var dbPath = CreateTempDbPath();
        var seasonId = Guid.NewGuid();
        var autoItemId = Guid.NewGuid();
        var userItemId = Guid.NewGuid();
        var episodeIds = Enumerable.Range(0, EpisodeIdCount - 2)
            .Select(_ => Guid.NewGuid())
            .Append(autoItemId)
            .Append(userItemId)
            .ToArray();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                db.DbSegment.AddRange(
                    new DbSegment(new Segment(autoItemId, new TimeRange(0, 10)), AnalysisMode.Introduction),
                    new DbSegment(new Segment(userItemId, new TimeRange(0, 10)), AnalysisMode.Credits, isUserProvided: true));
                db.DbSeasonState.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default, episodeIds));
                await db.SaveChangesAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.ResetSeasonForReanalysisAsync(seasonId, episodeIds, [AnalysisMode.Introduction, AnalysisMode.Credits]);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var remaining = await db.DbSegment.ToListAsync();
                var survivor = Assert.Single(remaining);
                Assert.Equal(userItemId, survivor.ItemId);
                Assert.True(survivor.IsUserProvided);

                var state = await db.DbSeasonState.SingleAsync(s => s.SeasonId == seasonId);
                Assert.Empty(state.EpisodeIds);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetSeasonQueueSnapshotAsync_HandlesEpisodeIdSetAboveSqliteParameterLimit()
    {
        const int EpisodeIdCount = 33_000;

        var dbPath = CreateTempDbPath();
        var seasonId = Guid.NewGuid();
        var episodeWithSegmentId = Guid.NewGuid();
        var episodeIds = Enumerable.Range(0, EpisodeIdCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(episodeWithSegmentId)
            .ToArray();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
                db.DbSegment.Add(new DbSegment(new Segment(episodeWithSegmentId, new TimeRange(0, 30)), AnalysisMode.Introduction));
                db.DbSeasonState.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default, [episodeWithSegmentId], "snapshot-config"));
                await db.SaveChangesAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var snapshot = await db.GetSeasonQueueSnapshotAsync(seasonId, episodeIds);
                Assert.True(snapshot.SegmentsByEpisodeId.TryGetValue(episodeWithSegmentId, out var segmentsByMode));
                Assert.True(segmentsByMode!.ContainsKey(AnalysisMode.Introduction));
                Assert.True(snapshot.ConfigHashByMode.TryGetValue(AnalysisMode.Introduction, out var configHash));
                Assert.Equal("snapshot-config", configHash);
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

            // The cache database has its own independent gate behind its own factory.
            var cacheOptions = new DbContextOptionsBuilder<DetectionCacheDbContext>()
                .UseSqlite($"Data Source={cacheDbPath}")
                .Options;
            var cacheFactory = new GatedDetectionCacheDbContextFactory(initializer, cacheOptions);
            var cacheDb = await cacheFactory.CreateDbContextAsync();
            await using (cacheDb.ConfigureAwait(false))
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

    [Fact]
    public async Task CacheInitializationFailure_DoesNotBlockSegmentDatabaseAccess()
    {
        // Fault-containment: the cache gate failing (here with an InvalidOperationException,
        // an exception type outside the old IOException/SqliteException filter) must neither
        // fault the segment gate nor propagate from EnsureInitializedAsync (which would abort
        // host startup via the hosted initialization service).
        var dbPath = CreateTempDbPath();

        try
        {
            var segmentOptions = new DbContextOptionsBuilder<IntroSkipperDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            // No provider configured: any use throws InvalidOperationException during init.
            var brokenCacheOptions = new DbContextOptionsBuilder<DetectionCacheDbContext>().Options;

            var initializer = new DatabaseInitializer(NullLogger<DatabaseInitializer>.Instance, segmentOptions, brokenCacheOptions);

            // The combined gate (used by the hosted service) must complete without throwing.
            await initializer.EnsureInitializedAsync();

            // The segment factory must still hand out fully migrated contexts.
            var factory = new GatedIntroSkipperDbContextFactory(initializer, segmentOptions);
            var db = await factory.CreateDbContextAsync();
            await using (db.ConfigureAwait(false))
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                await db.UpdateTimestampAsync(new Segment(Guid.NewGuid(), new TimeRange(1, 2)), AnalysisMode.Introduction);
                Assert.Equal(1, await db.DbSegment.CountAsync());
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
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
