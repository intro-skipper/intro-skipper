// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
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
/// Tests for the <see cref="IntroSkipperDatabase"/> and <see cref="DetectionCacheDatabase"/>
/// facades. The facades are constructed directly over temp-file SQLite databases —
/// no <c>Plugin.Instance</c> is required, which is the point of the design.
/// </summary>
public sealed class TestDatabaseFacades
{
    [Fact]
    public async Task InitializationGate_CreatesSchemaBeforeFirstQuery()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            // No EnsureCreated, no migrations — the facade's initialization gate must run
            // legacy repair + migrations before the first query touches the database.
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var segments = await database.GetSegmentsAsync(Guid.NewGuid());
            Assert.Empty(segments);

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UpdateTimestampAsync_AnalysisResultDoesNotOverwriteUserProvidedSegment()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            var userSegment = new Segment(itemId, new TimeRange(10, 60));
            await database.UpdateTimestampAsync(userSegment, AnalysisMode.Introduction, isUserProvided: true);

            var analyzed = new Segment(itemId, new TimeRange(20, 80));
            await database.UpdateTimestampAsync(analyzed, AnalysisMode.Introduction, isUserProvided: false);

            var stored = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.True(stored.IsUserProvided);
            Assert.Equal(10, stored.Start);
            Assert.Equal(60, stored.End);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UpdateTimestampAsync_UserProvidedSegmentReplacesAnalysisResult()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(20, 80)), AnalysisMode.Introduction);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 60)), AnalysisMode.Introduction, isUserProvided: true);

            var stored = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.True(stored.IsUserProvided);
            Assert.Equal(10, stored.Start);
            Assert.Equal(60, stored.End);
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
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // Store an intro: 0–90 s.
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(0, 90)), AnalysisMode.Introduction);

            var credits = new Segment(itemId, new TimeRange(creditsStart, creditsEnd));
            await database.UpdateTimestampAsync(credits, AnalysisMode.Credits, isUserProvided);

            var stored = await database.GetSegmentsAsync(itemId);
            Assert.Equal(expectedCount, stored.Count(s => s.Type == AnalysisMode.Credits));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CleanTimestampsAsync_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
    {
        // EF Core 10 translates parameterized collections on SQLite to discrete padded
        // parameters, and SQLite rejects statements above 32,766 variables, so this
        // verifies the facade binds the retained ID set as a single EF.Parameter JSON
        // parameter (json_each) above that limit.
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
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(retainedItemId, new TimeRange(0, 10)), AnalysisMode.Introduction);
            await database.UpdateTimestampAsync(new Segment(staleItemId, new TimeRange(20, 30)), AnalysisMode.Introduction);

            await database.CleanTimestampsAsync(enabledEpisodeIds);

            var retained = Assert.Single(await database.GetSegmentsAsync(retainedItemId));
            Assert.Equal(retainedItemId, retained.ItemId);
            Assert.Empty(await database.GetSegmentsAsync(staleItemId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteSegmentsForItemsAsync_DoesNotExceedSqliteVariableLimit_WhenItemListIsLarge()
    {
        const int LargeItemCount = 33_000;

        var dbPath = CreateTempDbPath();
        var targetItemId = Guid.NewGuid();
        var retainedItemId = Guid.NewGuid();
        var itemIds = Enumerable.Range(0, LargeItemCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(targetItemId)
            .ToArray();

        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(targetItemId, new TimeRange(0, 10)), AnalysisMode.Introduction);
            await database.UpdateTimestampAsync(new Segment(retainedItemId, new TimeRange(20, 30)), AnalysisMode.Introduction);

            var deleted = await database.DeleteSegmentsForItemsAsync(itemIds);

            Assert.Equal(1, deleted);
            Assert.Empty(await database.GetSegmentsAsync(targetItemId));
            Assert.Single(await database.GetSegmentsAsync(retainedItemId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DetectionCacheDatabase_StaleIdComputationAndChunkedDelete_HandleLargeLibraries()
    {
        const int LargeEpisodeCount = 33_000;

        var dbPath = CreateTempDbPath();
        var validItemId = Guid.NewGuid();
        var staleItemId = Guid.NewGuid();

        try
        {
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(dbPath);
            cacheDatabase.Upsert(validItemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10, EntrypointTestHelpers.EmptyJsonArray, string.Empty);
            cacheDatabase.Upsert(staleItemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10, EntrypointTestHelpers.EmptyJsonArray, string.Empty);

            var validItemIds = Enumerable.Range(0, LargeEpisodeCount - 1)
                .Select(_ => Guid.NewGuid())
                .Append(validItemId)
                .ToHashSet();

            var staleIds = await cacheDatabase.GetStaleItemIdsAsync(validItemIds);
            Assert.Equal([staleItemId], staleIds);

            // Delete with a large ID set (stale ID plus filler) to exercise the chunked path.
            var deleteIds = Enumerable.Range(0, LargeEpisodeCount - 1)
                .Select(_ => Guid.NewGuid())
                .Append(staleItemId)
                .ToArray();
            var deleted = await cacheDatabase.DeleteForItemsAsync(deleteIds);

            Assert.Equal(1, deleted);
            Assert.Null(cacheDatabase.FindEntry(staleItemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10));
            Assert.NotNull(cacheDatabase.FindEntry(validItemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UpdateTimestampAsync_AllowsMultipleCommercialSegmentsAndDeduplicates()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(0, 10)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(20, 30)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(20, 30)), AnalysisMode.Commercial);

            var stored = await database.GetSegmentsAsync(itemId);
            Assert.Equal(2, stored.Count(s => s.Type == AnalysisMode.Commercial));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CleanSeasonStateAsync_DoesNotExceedSqliteVariableLimit_WhenSeasonListIsLarge()
    {
        const int LargeSeasonCount = 33_000;

        var dbPath = CreateTempDbPath();
        var retainedSeasonId = Guid.NewGuid();
        var staleSeasonId = Guid.NewGuid();
        var retainedEpisodeId = Guid.NewGuid();
        var retainedSeasonIds = Enumerable.Range(0, LargeSeasonCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(retainedSeasonId)
            .ToArray();

        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.SetEpisodeIdsAsync(retainedSeasonId, AnalysisMode.Introduction, [retainedEpisodeId]);
            await database.SetEpisodeIdsAsync(staleSeasonId, AnalysisMode.Introduction, [Guid.NewGuid()]);

            await database.CleanSeasonStateAsync(retainedSeasonIds);

            await using var db = new IntroSkipperDbContext(dbPath);
            var retainedState = await db.DbSeasonState
                .AsNoTracking()
                .SingleAsync(s => s.SeasonId == retainedSeasonId);
            Assert.Equal([retainedEpisodeId], retainedState.EpisodeIds);
            Assert.False(await db.DbSeasonState.AnyAsync(s => s.SeasonId == staleSeasonId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GetSeasonQueueSnapshotAsync_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
    {
        const int LargeEpisodeCount = 33_000;

        var dbPath = CreateTempDbPath();
        var seasonId = Guid.NewGuid();
        var episodeWithSegmentId = Guid.NewGuid();
        var episodeIds = Enumerable.Range(0, LargeEpisodeCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(episodeWithSegmentId)
            .ToList();

        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(episodeWithSegmentId, new TimeRange(0, 30)), AnalysisMode.Introduction);
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, [episodeWithSegmentId], "snapshot-config");

            var snapshot = await database.GetSeasonQueueSnapshotAsync(seasonId, episodeIds);

            Assert.True(snapshot.EpisodeIdsByMode.TryGetValue(AnalysisMode.Introduction, out var analyzedIds));
            Assert.Contains(episodeWithSegmentId, analyzedIds!);
            Assert.True(snapshot.SegmentsByEpisodeId.TryGetValue(episodeWithSegmentId, out var segmentsByAnalysisMode));
            Assert.True(segmentsByAnalysisMode!.TryGetValue(AnalysisMode.Introduction, out _));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ResetSeasonForReanalysisAsync_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
    {
        const int LargeEpisodeCount = 33_000;

        var dbPath = CreateTempDbPath();
        var seasonId = Guid.NewGuid();
        var automaticEpisodeId = Guid.NewGuid();
        var userProvidedEpisodeId = Guid.NewGuid();
        var episodeIds = Enumerable.Range(0, LargeEpisodeCount - 2)
            .Select(_ => Guid.NewGuid())
            .Append(automaticEpisodeId)
            .Append(userProvidedEpisodeId)
            .ToArray();

        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(automaticEpisodeId, new TimeRange(0, 30)), AnalysisMode.Introduction);
            await database.UpdateTimestampAsync(new Segment(userProvidedEpisodeId, new TimeRange(0, 30)), AnalysisMode.Introduction, isUserProvided: true);
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, episodeIds);

            await database.ResetSeasonForReanalysisAsync(seasonId, episodeIds, [AnalysisMode.Introduction]);

            Assert.Empty(await database.GetSegmentsAsync(automaticEpisodeId));
            Assert.Single(await database.GetSegmentsAsync(userProvidedEpisodeId));
            await using var db = new IntroSkipperDbContext(dbPath);
            var seasonState = await db.DbSeasonState
                .AsNoTracking()
                .SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
            Assert.Empty(seasonState.EpisodeIds);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CleanStaleAutomaticSegmentsAsync_DoesNotExceedSqliteVariableLimit_WhenItemListIsLarge()
    {
        const int LargeItemCount = 33_000;

        var dbPath = CreateTempDbPath();
        var staleItemId = Guid.NewGuid();
        var itemIds = Enumerable.Range(0, LargeItemCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(staleItemId)
            .ToArray();

        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(staleItemId, new TimeRange(0, 30)), AnalysisMode.Introduction, configHash: "old-config");

            await database.CleanStaleAutomaticSegmentsAsync(itemIds, AnalysisMode.Introduction, "new-config");

            Assert.Empty(await database.GetSegmentsAsync(staleItemId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ConcurrentFacadeInstances_InitializeLegacyDatabaseWithoutErrors()
    {
        // Tests construct sibling facade instances with independent one-shot gates over
        // the same file, so both may run legacy repair + migrations concurrently on
        // first use. EnsureLegacySchemaCompatibility is existence-check-guarded and
        // transactional (BEGIN IMMEDIATE) and MigrateAsync takes EF's migration lock;
        // this pins that both succeed and the repaired schema is correct.
        var dbPath = CreateTempDbPath();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE "DbSeasonInfo" (
                        "SeasonId" TEXT NOT NULL,
                        "Type" INTEGER NOT NULL,
                        "Action" INTEGER NOT NULL DEFAULT 0,
                        "EpisodeIds" TEXT NOT NULL,
                        CONSTRAINT "PK_DbSeasonInfo" PRIMARY KEY ("SeasonId", "Type")
                    );
                    CREATE TABLE "DbSegment" (
                        "ItemId" TEXT NOT NULL,
                        "Type" INTEGER NOT NULL,
                        "Start" REAL NOT NULL DEFAULT 0.0,
                        "End" REAL NOT NULL DEFAULT 0.0,
                        CONSTRAINT "PK_DbSegment" PRIMARY KEY ("ItemId", "Type")
                    );
                    INSERT INTO "DbSeasonInfo" ("SeasonId", "Type", "Action", "EpisodeIds")
                    VALUES ($seasonId, $type, $action, $episodeIds);
                    INSERT INTO "DbSegment" ("ItemId", "Type", "Start", "End")
                    VALUES ($itemId, $segmentType, $start, $end);
                    """;
                command.Parameters.AddWithValue("$seasonId", seasonId.ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$type", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$action", (int)AnalyzerAction.Default);
                command.Parameters.AddWithValue("$episodeIds", $"[\"{episodeId}\"]");
                command.Parameters.AddWithValue("$itemId", episodeId.ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$segmentType", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$start", 0.0);
                command.Parameters.AddWithValue("$end", 30.0);
                await command.ExecuteNonQueryAsync();
            }

            var facadeA = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var facadeB = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // Trigger both initialization gates concurrently via first operations.
            var results = await Task.WhenAll(
                Task.Run(() => facadeA.GetSegmentsAsync(episodeId)),
                Task.Run(() => facadeB.GetSegmentsAsync(episodeId)));

            Assert.All(results, segments => Assert.Single(segments));

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var seasonState = await db.DbSeasonState.SingleAsync();
            Assert.Equal(seasonId, seasonState.SeasonId);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task Initialization_EnforcesWalJournalMode_OnSegmentDatabase()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            // Simulate a database created or rewritten by external tooling: a valid
            // SQLite file in the default rollback-journal mode. EF only switches to WAL
            // when *it* creates the database file, so initialization must enforce it.
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE \"ExternalToolMarker\" (\"Id\" INTEGER PRIMARY KEY)";
                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal("delete", GetJournalMode(dbPath));

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.InitializeAsync();

            Assert.Equal("wal", GetJournalMode(dbPath));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public void Initialization_EnforcesWalJournalMode_OnCacheDatabase()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            // A pre-existing empty database file: EnsureCreated sees an existing
            // database, creates only the tables, and never applies EF's create-time
            // WAL default — initialization must enforce it.
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
            }

            Assert.Equal("delete", GetJournalMode(dbPath));

            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(dbPath);
            cacheDatabase.Initialize();

            Assert.Equal("wal", GetJournalMode(dbPath));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    private static string GetJournalMode(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return (string)command.ExecuteScalar()!;
    }

    private static string CreateTempDbPath()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "database-facades");
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
