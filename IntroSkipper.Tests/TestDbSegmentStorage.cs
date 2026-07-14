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
    public async Task TimestampCleanup_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
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

                var staleEpisodeIds = await Plugin.GetStaleTimestampEpisodeIdsAsync(enabledEpisodeIds);
                Assert.Equal([staleItemId], staleEpisodeIds);

                using (var db = new IntroSkipperDbContext(dbPath))
                {
                    Assert.Equal(2, db.DbSegment.Count());
                }

                await Plugin.DeleteTimestampsAsync(staleEpisodeIds);
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
                await db.Database.EnsureCreatedAsync();
                db.DbSegment.Add(new DbSegment(
                    new Segment(episodeWithSegmentId, new TimeRange(0, 30)),
                    AnalysisMode.Introduction));
                db.DbSeasonState.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Introduction,
                    AnalyzerAction.Chromaprint,
                    [episodeWithSegmentId],
                    "snapshot-config"));
                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                // Should not throw even with 1001 episode IDs (above the SQLite 999-parameter limit).
                var snapshot = await Plugin.GetSeasonQueueSnapshotAsync(seasonId, episodeIds);
                Assert.True(snapshot.EpisodeIdsByMode.TryGetValue(AnalysisMode.Introduction, out var analyzedIds));
                Assert.Contains(episodeWithSegmentId, analyzedIds!);
                Assert.True(snapshot.ConfigHashByMode.TryGetValue(AnalysisMode.Introduction, out var configHash));
                Assert.Equal("snapshot-config", configHash);
                Assert.True(snapshot.AnalyzerActionByMode.TryGetValue(AnalysisMode.Introduction, out var analyzerAction));
                Assert.Equal(AnalyzerAction.Chromaprint, analyzerAction);


                Assert.True(snapshot.SegmentsByEpisodeId.TryGetValue(episodeWithSegmentId, out var segmentsByAnalysisMode));
                Assert.True(segmentsByAnalysisMode!.TryGetValue(AnalysisMode.Introduction, out _));
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task EnsureConfigHashColumns_AddsMissingColumnsForLegacySchema()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

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
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_DbSegment" PRIMARY KEY AUTOINCREMENT,
                        "ItemId" TEXT NOT NULL,
                        "Type" INTEGER NOT NULL,
                        "Start" REAL NOT NULL DEFAULT 0.0,
                        "End" REAL NOT NULL DEFAULT 0.0,
                        "IsUserProvided" INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO "DbSeasonInfo" ("SeasonId", "Type", "Action", "EpisodeIds")
                    VALUES ($seasonId, $type, $action, $episodeIds);
                    INSERT INTO "DbSegment" ("ItemId", "Type", "Start", "End", "IsUserProvided")
                    VALUES ($itemId, $segmentType, $start, $end, $isUserProvided);
                    """;
                command.Parameters.AddWithValue("$seasonId", seasonId.ToString());
                command.Parameters.AddWithValue("$type", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$action", (int)AnalyzerAction.Default);
                command.Parameters.AddWithValue("$episodeIds", $"[\"{episodeId}\"]");
                command.Parameters.AddWithValue("$itemId", episodeId.ToString());
                command.Parameters.AddWithValue("$segmentType", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$start", 0.0);
                command.Parameters.AddWithValue("$end", 30.0);
                command.Parameters.AddWithValue("$isUserProvided", false);
                await command.ExecuteNonQueryAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                db.EnsureConfigHashColumns();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var seasonState = await db.DbSeasonState.SingleAsync();
                var segment = await db.DbSegment.SingleAsync();

                Assert.Equal(string.Empty, seasonState.ConfigHash);
                Assert.Empty(seasonState.SettledReanalysisEpisodeIds);
                Assert.Equal(string.Empty, segment.ConfigHash);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task EnsureLegacySchemaCompatibility_UpgradesInitialSchemaWithoutDataLoss()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

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
                    CREATE INDEX "IX_DbSeasonInfo_SeasonId" ON "DbSeasonInfo" ("SeasonId");
                    CREATE INDEX "IX_DbSegment_ItemId" ON "DbSegment" ("ItemId");
                    INSERT INTO "DbSeasonInfo" ("SeasonId", "Type", "Action", "EpisodeIds")
                    VALUES ($seasonId, $type, $action, $episodeIds);
                    INSERT INTO "DbSegment" ("ItemId", "Type", "Start", "End")
                    VALUES ($itemId, $segmentType, $start, $end);
                    """;
                command.Parameters.AddWithValue("$seasonId", seasonId.ToString());
                command.Parameters.AddWithValue("$type", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$action", (int)AnalyzerAction.Default);
                command.Parameters.AddWithValue("$episodeIds", $"[\"{episodeId}\"]");
                command.Parameters.AddWithValue("$itemId", episodeId.ToString());
                command.Parameters.AddWithValue("$segmentType", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$start", 0.0);
                command.Parameters.AddWithValue("$end", 30.0);
                await command.ExecuteNonQueryAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                db.EnsureLegacySchemaCompatibility();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
                Assert.Empty(pendingMigrations);

                var seasonState = await db.DbSeasonState.SingleAsync();
                var segment = await db.DbSegment.SingleAsync();

                Assert.Equal(seasonId, seasonState.SeasonId);
                Assert.Equal(AnalysisMode.Introduction, seasonState.Type);
                Assert.Equal(AnalyzerAction.Default, seasonState.Action);
                Assert.Equal(new[] { episodeId }, seasonState.EpisodeIds);
                Assert.Equal(string.Empty, seasonState.ConfigHash);
                Assert.Empty(seasonState.SettledReanalysisEpisodeIds);
                Assert.Equal(string.Empty, segment.ConfigHash);
                Assert.False(segment.IsUserProvided);
                Assert.True(segment.Id > 0);
                Assert.Equal(episodeId, segment.ItemId);
                Assert.Equal(AnalysisMode.Introduction, segment.Type);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task EnsureLegacySchemaCompatibility_MigratesSeasonInfoToSeasonState()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var seasonWithoutReanalysisId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var episodeWithoutReanalysisId = Guid.NewGuid();

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
                        "ConfigHash" TEXT NOT NULL DEFAULT '',
                        CONSTRAINT "PK_DbSeasonInfo" PRIMARY KEY ("SeasonId", "Type")
                    );
                    INSERT INTO "DbSeasonInfo" ("SeasonId", "Type", "Action", "EpisodeIds", "ConfigHash")
                    VALUES ($seasonId, $introType, $action, $episodeIds, $configHash),
                           ($seasonWithoutReanalysisId, $introType, $action, $episodeWithoutReanalysisIds, $configHash);
                    """;
                command.Parameters.AddWithValue("$seasonId", seasonId.ToString());
                command.Parameters.AddWithValue("$seasonWithoutReanalysisId", seasonWithoutReanalysisId.ToString());
                command.Parameters.AddWithValue("$introType", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$action", (int)AnalyzerAction.Chromaprint);
                command.Parameters.AddWithValue("$episodeIds", $"[\"{episodeId}\"]");
                command.Parameters.AddWithValue("$episodeWithoutReanalysisIds", $"[\"{episodeWithoutReanalysisId}\"]");
                command.Parameters.AddWithValue("$configHash", "intro-config");
                await command.ExecuteNonQueryAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                db.EnsureLegacySchemaCompatibility();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var states = await db.DbSeasonState.OrderBy(s => s.SeasonId).ThenBy(s => s.Type).ToListAsync();
                Assert.Equal(2, states.Count);

                var introduction = states.Single(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
                Assert.Equal(seasonId, introduction.SeasonId);
                Assert.Equal(AnalyzerAction.Chromaprint, introduction.Action);
                Assert.Equal(new[] { episodeId }, introduction.EpisodeIds);
                Assert.Equal("intro-config", introduction.ConfigHash);
                Assert.Empty(introduction.SettledReanalysisEpisodeIds);

                var noReanalysis = states.Single(s => s.SeasonId == seasonWithoutReanalysisId);
                Assert.Equal(AnalysisMode.Introduction, noReanalysis.Type);
                Assert.Equal(new[] { episodeWithoutReanalysisId }, noReanalysis.EpisodeIds);
                Assert.Empty(noReanalysis.SettledReanalysisEpisodeIds);

                Assert.False(await TableExistsAsync(db, "DbSeasonInfo"));
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ApplyMigrations_PreservesSeasonInfoRowsFromPreviousMigration()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

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
                        "ConfigHash" TEXT NOT NULL DEFAULT '',
                        CONSTRAINT "PK_DbSeasonInfo" PRIMARY KEY ("SeasonId", "Type")
                    );
                    CREATE INDEX "IX_DbSeasonInfo_SeasonId" ON "DbSeasonInfo" ("SeasonId");
                    CREATE TABLE "DbSegment" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_DbSegment" PRIMARY KEY AUTOINCREMENT,
                        "ItemId" TEXT NOT NULL,
                        "Type" INTEGER NOT NULL,
                        "Start" REAL NOT NULL DEFAULT 0.0,
                        "End" REAL NOT NULL DEFAULT 0.0,
                        "IsUserProvided" INTEGER NOT NULL DEFAULT 0,
                        "ConfigHash" TEXT NOT NULL DEFAULT ''
                    );
                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                        "ProductVersion" TEXT NOT NULL
                    );
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES
                        ('20241116153434_InitialCreate', '9.0.11'),
                        ('20260309205737_AddIsUserProvided', '9.0.11'),
                        ('20260314184512_AddDbSegmentIdentity', '9.0.11'),
                        ('20260316060001_AddNonCommercialUniqueIndex', '9.0.11'),
                        ('20260519073000_AddConfigHashes', '9.0.11');
                    INSERT INTO "DbSeasonInfo" ("SeasonId", "Type", "Action", "EpisodeIds", "ConfigHash")
                    VALUES ($seasonId, $type, $action, $episodeIds, $configHash);
                    """;
                command.Parameters.AddWithValue("$seasonId", seasonId.ToString());
                command.Parameters.AddWithValue("$type", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$action", (int)AnalyzerAction.Chromaprint);
                command.Parameters.AddWithValue("$episodeIds", $"[\"{episodeId}\"]");
                command.Parameters.AddWithValue("$configHash", "intro-config");
                await command.ExecuteNonQueryAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.ApplyMigrationsAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
                Assert.Empty(pendingMigrations);

                var states = await db.DbSeasonState.OrderBy(s => s.Type).ToListAsync();
                Assert.Single(states);

                var seasonState = states.Single(s => s.Type == AnalysisMode.Introduction);
                Assert.Equal(seasonId, seasonState.SeasonId);
                Assert.Equal(AnalyzerAction.Chromaprint, seasonState.Action);
                Assert.Equal(new[] { episodeId }, seasonState.EpisodeIds);
                Assert.Equal("intro-config", seasonState.ConfigHash);
                Assert.Empty(seasonState.SettledReanalysisEpisodeIds);

                Assert.False(await TableExistsAsync(db, "DbSeasonInfo"));
            }
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

                db.DbSeasonState.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Introduction,
                    AnalyzerAction.Default,
                    [episodeId],
                    "season-config"));
                db.DbSegment.Add(new DbSegment(
                    new Segment(episodeId, new TimeRange(0, 30)),
                    AnalysisMode.Introduction,
                    false,
                    "segment-config"));
                await db.SaveChangesAsync();
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var seasonState = await db.DbSeasonState.SingleAsync();
                var segment = await db.DbSegment.SingleAsync();

                Assert.Equal("season-config", seasonState.ConfigHash);
                Assert.Empty(seasonState.SettledReanalysisEpisodeIds);
                Assert.Equal(new[] { episodeId }, seasonState.EpisodeIds);
                Assert.Equal("segment-config", segment.ConfigHash);
                Assert.False(await TableExistsAsync(db, "DbSeasonInfo"));
                Assert.True(segment.Id > 0);
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
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        var dbFileName = Guid.NewGuid().ToString("N") + ".db";
        if (Path.IsPathRooted(dbFileName))
        {
            throw new ArgumentException("dbFileName must be a relative file name.", nameof(dbFileName));
        }
        var dbPath = Path.Join(tempDir, dbFileName);
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

    private static async Task<bool> TableExistsAsync(IntroSkipperDbContext db, string tableName)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await db.Database.OpenConnectionAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync() is not null;
        }
        finally
        {
            if (!wasOpen)
            {
                await db.Database.CloseConnectionAsync();
            }
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
