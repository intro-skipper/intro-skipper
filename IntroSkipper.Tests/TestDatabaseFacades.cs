// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task GetTimestampsAsync_SelectsEarliestStartPerMode()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // Multiple commercial segments, deliberately inserted out of Start order,
            // plus an intro for the same episode.
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(50, 60)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(20, 30)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(100, 110)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(5, 40)), AnalysisMode.Introduction);

            var timestamps = await database.GetTimestampsAsync(itemId);

            // One representative per mode; for modes with several rows the
            // earliest-start segment wins (GroupBy-Type → min-Start-first).
            Assert.Equal(2, timestamps.Count);
            Assert.Equal(20, timestamps[AnalysisMode.Commercial].Start);
            Assert.Equal(30, timestamps[AnalysisMode.Commercial].End);
            Assert.Equal(5, timestamps[AnalysisMode.Introduction].Start);
            Assert.Equal(40, timestamps[AnalysisMode.Introduction].End);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteTimestampAsync_WithSegment_RemovesOnlyEpsilonMatchingEntryOfThatMode()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(30, 40)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(0, 5)), AnalysisMode.Introduction);

            // Outside the 0.001 epsilon: nothing matches, nothing is removed.
            await database.DeleteTimestampAsync(itemId, AnalysisMode.Commercial, new Segment(itemId, new TimeRange(10.01, 20)));

            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.Equal(3, await db.DbSegment.AsNoTracking().CountAsync(s => s.ItemId == itemId));
            }

            // Within epsilon on both bounds: removes exactly the matching commercial,
            // leaving the other commercial and the intro untouched.
            await database.DeleteTimestampAsync(itemId, AnalysisMode.Commercial, new Segment(itemId, new TimeRange(10.0005, 20.0005)));

            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                var remaining = await db.DbSegment.AsNoTracking().Where(s => s.ItemId == itemId).ToListAsync();
                Assert.Equal(2, remaining.Count);
                var commercial = Assert.Single(remaining, s => s.Type == AnalysisMode.Commercial);
                Assert.Equal(30, commercial.Start);
                Assert.Equal(40, commercial.End);
                Assert.Single(remaining, s => s.Type == AnalysisMode.Introduction);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteTimestampAsync_WithoutSegment_CommercialIsNoOpButNonCommercialDeletesMode()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(10, 20)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(0, 5)), AnalysisMode.Introduction);

            // Commercial without a segment argument takes the early-return branch:
            // deleting "the" commercial is ambiguous, so nothing may be removed.
            await database.DeleteTimestampAsync(itemId, AnalysisMode.Commercial);

            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.Equal(2, await db.DbSegment.AsNoTracking().CountAsync(s => s.ItemId == itemId));
            }

            // Non-commercial without a segment argument deletes the whole mode.
            await database.DeleteTimestampAsync(itemId, AnalysisMode.Introduction);

            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                var remaining = Assert.Single(await db.DbSegment.AsNoTracking().Where(s => s.ItemId == itemId).ToListAsync());
                Assert.Equal(AnalysisMode.Commercial, remaining.Type);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteSegmentsByModeAsync_RemovesOnlyTheGivenMode()
    {
        var dbPath = CreateTempDbPath();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(itemA, new TimeRange(0, 30)), AnalysisMode.Introduction);
            await database.UpdateTimestampAsync(new Segment(itemA, new TimeRange(1200, 1260)), AnalysisMode.Credits);
            await database.UpdateTimestampAsync(new Segment(itemB, new TimeRange(0, 20)), AnalysisMode.Introduction);
            await database.UpdateTimestampAsync(new Segment(itemB, new TimeRange(100, 110)), AnalysisMode.Commercial);

            await database.DeleteSegmentsByModeAsync(AnalysisMode.Introduction);

            await using var db = new IntroSkipperDbContext(dbPath);
            var remaining = await db.DbSegment.AsNoTracking().ToListAsync();
            Assert.Equal(2, remaining.Count);
            Assert.DoesNotContain(remaining, s => s.Type == AnalysisMode.Introduction);
            Assert.Single(remaining, s => s.ItemId == itemA && s.Type == AnalysisMode.Credits);
            Assert.Single(remaining, s => s.ItemId == itemB && s.Type == AnalysisMode.Commercial);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteItemSegmentsAsync_RemovesAllSegmentsForTheItemOnly()
    {
        var dbPath = CreateTempDbPath();
        var targetItemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(targetItemId, new TimeRange(0, 30)), AnalysisMode.Introduction);
            await database.UpdateTimestampAsync(new Segment(targetItemId, new TimeRange(1200, 1260)), AnalysisMode.Credits);
            await database.UpdateTimestampAsync(new Segment(targetItemId, new TimeRange(100, 110)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(otherItemId, new TimeRange(0, 20)), AnalysisMode.Introduction);

            await database.DeleteItemSegmentsAsync(targetItemId);

            await using var db = new IntroSkipperDbContext(dbPath);
            var remaining = Assert.Single(await db.DbSegment.AsNoTracking().ToListAsync());
            Assert.Equal(otherItemId, remaining.ItemId);
            Assert.Equal(AnalysisMode.Introduction, remaining.Type);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ClearSeasonAnalysisAsync_DeletesSegmentsAndClearsSeasonState()
    {
        var dbPath = CreateTempDbPath();
        var seasonId = Guid.NewGuid();
        var otherSeasonId = Guid.NewGuid();
        var targetItemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(targetItemId, new TimeRange(0, 30)), AnalysisMode.Introduction);
            await database.UpdateTimestampAsync(new Segment(otherItemId, new TimeRange(0, 20)), AnalysisMode.Introduction);
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, [targetItemId]);
            await database.SetEpisodeIdsAsync(otherSeasonId, AnalysisMode.Introduction, [otherItemId]);

            await database.ClearSeasonAnalysisAsync(seasonId, [targetItemId]);

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.False(await db.DbSegment.AnyAsync(s => s.ItemId == targetItemId));
            Assert.True(await db.DbSegment.AnyAsync(s => s.ItemId == otherItemId));
            var targetState = await db.DbSeasonState.SingleAsync(s => s.SeasonId == seasonId);
            Assert.Empty(targetState.EpisodeIds);
            var otherState = await db.DbSeasonState.SingleAsync(s => s.SeasonId == otherSeasonId);
            Assert.Equal([otherItemId], otherState.EpisodeIds);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task AnalyzerActions_ReturnStoredRow_AndFillMissingModesWithDefault()
    {
        var dbPath = CreateTempDbPath();
        var seasonId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.SetAnalyzerActionAsync(
                seasonId,
                new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Chromaprint });

            Assert.Equal(AnalyzerAction.Chromaprint, await database.GetAnalyzerActionAsync(seasonId, AnalysisMode.Introduction));
            Assert.Equal(AnalyzerAction.Default, await database.GetAnalyzerActionAsync(seasonId, AnalysisMode.Credits));
            Assert.Equal(AnalyzerAction.Default, await database.GetAnalyzerActionAsync(Guid.NewGuid(), AnalysisMode.Introduction));

            var allActions = await database.GetAllAnalyzerActionsAsync(seasonId);
            Assert.Equal(Enum.GetValues<AnalysisMode>().Length, allActions.Count);
            foreach (var mode in Enum.GetValues<AnalysisMode>())
            {
                Assert.Equal(
                    mode == AnalysisMode.Introduction ? AnalyzerAction.Chromaprint : AnalyzerAction.Default,
                    allActions[mode]);
            }
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
    public async Task DetectionCacheDatabase_StaleIdComputationAndParameterizedDelete_HandleLargeLibraries()
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

            // Delete with a large ID set (stale ID plus filler) to exercise the
            // single-JSON-parameter path.
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
    public async Task CleanStaleAutomaticSegmentsAsync_DeletesOnlyStaleAutomaticSegments_WhenItemListIsLarge()
    {
        const int LargeItemCount = 33_000;

        var dbPath = CreateTempDbPath();
        var staleItemId = Guid.NewGuid();
        var userProvidedItemId = Guid.NewGuid();
        var matchingHashItemId = Guid.NewGuid();
        var otherModeItemId = Guid.NewGuid();
        var itemIds = Enumerable.Range(0, LargeItemCount - 4)
            .Select(_ => Guid.NewGuid())
            .Append(staleItemId)
            .Append(userProvidedItemId)
            .Append(matchingHashItemId)
            .Append(otherModeItemId)
            .ToArray();

        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // All four retention classes: only the stale-hash automatic segment of the
            // cleaned mode may be deleted.
            await database.UpdateTimestampAsync(new Segment(staleItemId, new TimeRange(0, 30)), AnalysisMode.Introduction, configHash: "old-config");
            await database.UpdateTimestampAsync(new Segment(userProvidedItemId, new TimeRange(0, 30)), AnalysisMode.Introduction, isUserProvided: true, configHash: "old-config");
            await database.UpdateTimestampAsync(new Segment(matchingHashItemId, new TimeRange(0, 30)), AnalysisMode.Introduction, configHash: "new-config");
            await database.UpdateTimestampAsync(new Segment(otherModeItemId, new TimeRange(1200, 1260)), AnalysisMode.Credits, configHash: "old-config");

            await database.CleanStaleAutomaticSegmentsAsync(itemIds, AnalysisMode.Introduction, "new-config");

            Assert.Empty(await database.GetSegmentsAsync(staleItemId));

            await using var db = new IntroSkipperDbContext(dbPath);
            var remaining = await db.DbSegment.AsNoTracking().ToListAsync();
            Assert.Equal(3, remaining.Count);
            Assert.Single(remaining, s => s.ItemId == userProvidedItemId && s.Type == AnalysisMode.Introduction && s.IsUserProvided);
            Assert.Single(remaining, s => s.ItemId == matchingHashItemId && s.Type == AnalysisMode.Introduction && s.ConfigHash == "new-config");
            Assert.Single(remaining, s => s.ItemId == otherModeItemId && s.Type == AnalysisMode.Credits);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RebuildDatabaseAsync_PreservesValidSegmentsAndSeasonStates()
    {
        var dbPath = CreateTempDbPath();
        var automaticItemId = Guid.NewGuid();
        var userProvidedItemId = Guid.NewGuid();
        var invalidItemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(automaticItemId, new TimeRange(5, 30)), AnalysisMode.Introduction, configHash: "cfg-auto");
            await database.UpdateTimestampAsync(new Segment(automaticItemId, new TimeRange(100, 110)), AnalysisMode.Commercial);
            await database.UpdateTimestampAsync(new Segment(userProvidedItemId, new TimeRange(1200, 1260)), AnalysisMode.Credits, isUserProvided: true);
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, episodeIds, "cfg-season");
            await database.SetAnalyzerActionAsync(
                seasonId,
                new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Credits] = AnalyzerAction.None });

            // An invalid segment (End <= 0, Segment.Valid == false) is seeded raw — the
            // facade write paths never produce one — and must be dropped by the rebuild's
            // backup filter.
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO "DbSegment" ("ItemId", "Type", "Start", "End", "IsUserProvided", "ConfigHash")
                    VALUES ($itemId, $type, $start, $end, 0, '')
                    """;
                command.Parameters.AddWithValue("$itemId", invalidItemId.ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$type", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$start", 10.0);
                command.Parameters.AddWithValue("$end", 0.0);
                await command.ExecuteNonQueryAsync();
            }

            await database.RebuildDatabaseAsync();

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            var segments = await db.DbSegment.AsNoTracking().ToListAsync();
            Assert.Equal(3, segments.Count);
            Assert.DoesNotContain(segments, s => s.ItemId == invalidItemId);
            var automatic = Assert.Single(segments, s => s.Type == AnalysisMode.Introduction);
            Assert.Equal(automaticItemId, automatic.ItemId);
            Assert.Equal("cfg-auto", automatic.ConfigHash);
            Assert.False(automatic.IsUserProvided);
            var userProvided = Assert.Single(segments, s => s.Type == AnalysisMode.Credits);
            Assert.Equal(userProvidedItemId, userProvided.ItemId);
            Assert.True(userProvided.IsUserProvided);
            Assert.Single(segments, s => s.Type == AnalysisMode.Commercial);

            var seasonStates = await db.DbSeasonState.AsNoTracking().Where(s => s.SeasonId == seasonId).ToListAsync();
            Assert.Equal(2, seasonStates.Count);
            var introState = Assert.Single(seasonStates, s => s.Type == AnalysisMode.Introduction);
            Assert.Equal(episodeIds, introState.EpisodeIds);
            Assert.Equal("cfg-season", introState.ConfigHash);
            var creditsState = Assert.Single(seasonStates, s => s.Type == AnalysisMode.Credits);
            Assert.Equal(AnalyzerAction.None, creditsState.Action);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RebuildDatabaseAsync_BackupFailure_AbortsAndPreservesFile_WithoutForceClean()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(5, 30)), AnalysisMode.Introduction);

            // Deterministic backup failure: dropping a column the model SELECT expects
            // makes the backup read throw SqliteException ("no such column"). The drop
            // happens after this facade's successful init gate has completed, so the
            // legacy-schema repair cannot undo it before the rebuild runs.
            await DropDbSegmentConfigHashColumnAsync(dbPath);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => database.RebuildDatabaseAsync());
            Assert.IsType<SqliteException>(exception.InnerException);

            // The aborted rebuild must not have touched the database file: the seeded
            // row is still there. (Raw connection — the EF model no longer matches.)
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM \"DbSegment\"";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RebuildDatabaseAsync_BackupFailure_RebuildsClean_WhenForceCleanRequested()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(5, 30)), AnalysisMode.Introduction);

            await DropDbSegmentConfigHashColumnAsync(dbPath);

            await database.RebuildDatabaseAsync(forceCleanOnBackupFailure: true);

            // Explicitly requested clean rebuild: the unreadable data is gone and the
            // schema is recreated at the current migration level.
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.Empty(await db.DbSegment.AsNoTracking().ToListAsync());
                Assert.Empty(await db.DbSeasonState.AsNoTracking().ToListAsync());
            }

            // The facade stays operational over the recreated database.
            await database.UpdateTimestampAsync(new Segment(itemId, new TimeRange(0, 10)), AnalysisMode.Introduction);
            Assert.Single(await database.GetSegmentsAsync(itemId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DatabaseOperation_InitializationFailure_DoesNotQueryAndNextOperationRetries()
    {
        // A data source whose parent directory does not exist cannot be opened or
        // created by SQLite, so the first initialization attempt fails.
        var dbPath = Path.Join(
            Path.GetTempPath(),
            "IntroSkipper.Tests",
            "database-facades",
            Guid.NewGuid().ToString("N") + "-missing-dir",
            "segments.db");
        var directory = Path.GetDirectoryName(dbPath)!;
        var contextCreations = 0;
        var database = new IntroSkipperDatabase(
            new TestDbContextFactory<IntroSkipperDbContext>(() =>
            {
                Interlocked.Increment(ref contextCreations);
                return new IntroSkipperDbContext(dbPath);
            }),
            NullLogger.Instance);

        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => database.GetSegmentsAsync(Guid.NewGuid()));

            // Only the initialization context was created; the operation did not run a
            // query against an unverified schema.
            Assert.Equal(1, Volatile.Read(ref contextCreations));

            Directory.CreateDirectory(directory);

            Assert.Empty(await database.GetSegmentsAsync(Guid.NewGuid()));
            Assert.Equal(3, Volatile.Read(ref contextCreations)); // Retry context + query context.

            await database.InitializeAsync();
            Assert.Equal(3, Volatile.Read(ref contextCreations)); // Successful gate stays cached.
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CacheOperation_InitializationFailure_DoesNotQueryAndNextOperationRetries()
    {
        var dbPath = Path.Join(
            Path.GetTempPath(),
            "IntroSkipper.Tests",
            "database-facades",
            Guid.NewGuid().ToString("N") + "-missing-dir",
            "cache.db");
        var directory = Path.GetDirectoryName(dbPath)!;
        var contextCreations = 0;
        var database = new DetectionCacheDatabase(
            new TestDbContextFactory<DetectionCacheDbContext>(() =>
            {
                Interlocked.Increment(ref contextCreations);
                return new DetectionCacheDbContext(dbPath);
            }),
            NullLogger.Instance);

        try
        {
            Assert.Throws<SqliteException>(() => database.FindEntry(
                Guid.NewGuid(), AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));

            Assert.Equal(1, Volatile.Read(ref contextCreations));

            Directory.CreateDirectory(directory);

            Assert.Null(database.FindEntry(
                Guid.NewGuid(), AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));
            Assert.Equal(3, Volatile.Read(ref contextCreations)); // Retry context + query context.

            database.Initialize();
            Assert.Equal(3, Volatile.Read(ref contextCreations)); // Successful gate stays cached.
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RetryableInitializationGate_ConcurrentCallersShareFailedAttemptAndNextAttemptRetries()
    {
        var attempts = 0;
        var initializationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new RetryableInitializationGate<Task>(InitializeAttemptAsync);

        var firstAttempt = gate.GetAttempt();
        var secondAttempt = gate.GetAttempt();
        Assert.Same(firstAttempt, secondAttempt);

        var first = firstAttempt.Value;
        var second = secondAttempt.Value;
        Assert.Same(first, second);

        await initializationEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        releaseInitialization.SetResult();

        var exceptions = await Task.WhenAll(
                Record.ExceptionAsync(() => first),
                Record.ExceptionAsync(() => second))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(exceptions, exception => Assert.IsType<IOException>(exception));
        Assert.Equal(1, Volatile.Read(ref attempts));

        Assert.True(gate.ResetIfCurrent(firstAttempt));
        Assert.False(gate.ResetIfCurrent(secondAttempt));

        var retryAttempt = gate.GetAttempt();
        Assert.NotSame(firstAttempt, retryAttempt);
        await retryAttempt.Value;
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Same(retryAttempt, gate.GetAttempt());

        async Task InitializeAttemptAsync()
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                initializationEntered.SetResult();
                await releaseInitialization.Task.ConfigureAwait(false);
                throw new IOException("Simulated transient database failure.");
            }
        }
    }

    [Fact]
    public async Task ConcurrentFacadeInstances_InitializeLegacyDatabaseWithoutErrors()
    {
        // Tests construct sibling facade instances with independent retryable gates over
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

            // Trigger both initialization gates concurrently via first operations. A
            // start barrier ensures both workers are running and parked before either
            // touches its facade, so the two gates genuinely overlap instead of racing
            // opportunistically. The barrier only ever releases work — it cannot
            // deadlock or fail spuriously.
            var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var taskA = Task.Run(async () =>
            {
                readyA.SetResult();
                await startBarrier.Task;
                return await facadeA.GetSegmentsAsync(episodeId);
            });
            var taskB = Task.Run(async () =>
            {
                readyB.SetResult();
                await startBarrier.Task;
                return await facadeB.GetSegmentsAsync(episodeId);
            });

            await Task.WhenAll(readyA.Task, readyB.Task);
            startBarrier.SetResult();

            var results = await Task.WhenAll(taskA, taskB);

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

    private static async Task DropDbSegmentConfigHashColumnAsync(string dbPath)
    {
        // Clear pooled handles first so the schema change cannot hit a stale lock.
        SqliteConnection.ClearAllPools();

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE \"DbSegment\" DROP COLUMN \"ConfigHash\"";
        await command.ExecuteNonQueryAsync();
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
