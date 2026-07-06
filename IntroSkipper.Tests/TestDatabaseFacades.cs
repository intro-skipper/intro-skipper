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
        // verifies the facade's chunked delete path above that limit.
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
