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
using static IntroSkipper.Tests.DatabaseTestHelpers;

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
            // migrations + the one-time legacy import before the first query touches the
            // database.
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var segments = await database.GetSegmentsAsync(Guid.NewGuid());
            Assert.Empty(segments);

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            // With no legacy file next to the database, the import question is answered
            // once and recorded, so later restarts never reconsider it.
            var marker = Assert.Single(await db.ImportHistory.AsNoTracking().ToListAsync());
            Assert.False(marker.SourceFileFound);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ReplaceAutoSegmentsAsync_DoesNotOverwriteOverlappingUserSegment()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.AddUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(10), Ticks(60));

            var analyzed = new Segment(itemId, new TimeRange(20, 80));
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [analyzed], SegmentSource.Chromaprint);

            var stored = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.True(stored.IsUserProvided);
            Assert.Equal(Ticks(10), stored.StartTicks);
            Assert.Equal(Ticks(60), stored.EndTicks);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Theory]
    [InlineData(100.0, 120.0)] // disjoint range elsewhere in the episode
    [InlineData(20.0, 30.0)]   // starts exactly at the user segment's end — touching is not overlap
    [InlineData(0.0, 10.0)]    // ends exactly at the user segment's start — touching is not overlap
    public async Task ReplaceAutoSegmentsAsync_AcceptsAutoSegmentNotOverlappingUserSegment(double autoStart, double autoEnd)
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.AddUserSegmentAsync(itemId, AnalysisMode.Commercial, Ticks(10), Ticks(20));

            // Analysis may still contribute segments of the same mode as long as they do
            // not strictly overlap the user's row — exactly abutting it is fine.
            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Commercial,
                [new Segment(itemId, new TimeRange(autoStart, autoEnd))],
                SegmentSource.Chapter);

            var stored = await database.GetSegmentsAsync(itemId);
            Assert.Equal(2, stored.Count);
            Assert.Single(stored, s => s.IsUserProvided);
            Assert.Single(stored, s => s.Source == SegmentSource.Chapter && s.StartTicks == Ticks(autoStart));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ReplaceUserSegmentAsync_ReplacesAnalysisResult()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(20, 80))], SegmentSource.Chromaprint);
            await database.ReplaceUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(10), Ticks(60));

            var stored = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.True(stored.IsUserProvided);
            Assert.Equal(Ticks(10), stored.StartTicks);
            Assert.Equal(Ticks(60), stored.EndTicks);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ReplaceUserSegmentAsync_SwapsExactRangeWithExistingActiveRow()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chromaprint);

            // Delete-all-active + insert of the IDENTICAL quadruple in one SaveChanges:
            // EF must order the delete before the insert (unique-value edges) or this throws.
            var row = await database.ReplaceUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(10), Ticks(60));

            var stored = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
            Assert.Equal(row.Id, stored.Id);
            Assert.Equal(SegmentSource.User, stored.Source);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task AddUserSegmentAsync_PromotesExactMatchingAutoRowInPlace()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chapter);
            var autoRow = Assert.Single(await database.GetSegmentsAsync(itemId));

            var promoted = await database.AddUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(10), Ticks(60));

            // Same row, same id — the auto row was promoted instead of duplicated.
            Assert.Equal(autoRow.Id, promoted.Id);
            Assert.Equal(SegmentSource.User, promoted.Source);
            Assert.Single(await database.GetSegmentsAsync(itemId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ReplaceAutoSegmentsAsync_KeepsIdsOfUnchangedRows()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Commercial,
                [new Segment(itemId, new TimeRange(10, 20)), new Segment(itemId, new TimeRange(50, 60))],
                SegmentSource.Chapter,
                "hash-1");
            var before = await database.GetSegmentsAsync(itemId);
            var unchangedId = Assert.Single(before, s => s.StartTicks == Ticks(10)).Id;

            // Re-analysis: one boundary unchanged, one moved.
            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Commercial,
                [new Segment(itemId, new TimeRange(10, 20)), new Segment(itemId, new TimeRange(55, 65))],
                SegmentSource.Chapter,
                "hash-2");

            var after = await database.GetSegmentsAsync(itemId);
            Assert.Equal(2, after.Count);

            // The unchanged range keeps its id (and picks up the new config hash); the
            // moved range gets a fresh row.
            var kept = Assert.Single(after, s => s.StartTicks == Ticks(10));
            Assert.Equal(unchangedId, kept.Id);
            Assert.Equal("hash-2", kept.ConfigHash);
            Assert.Single(after, s => s.StartTicks == Ticks(55));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Theory]
    [InlineData(0.0, 90.0, 60.0, 1440.0, false, 0)]   // overlapping, auto-detected → rejected
    [InlineData(0.0, 90.0, 1200.0, 1440.0, false, 1)] // non-overlapping, auto-detected → accepted
    [InlineData(0.0, 90.0, 60.0, 1440.0, true, 1)]    // overlapping, user-provided → accepted
    [InlineData(0.0, 90.0, 90.0, 200.0, false, 1)]    // touches intro end → accepted
    [InlineData(100.0, 200.0, 0.0, 100.0, false, 1)]  // touches intro start → accepted
    public async Task ReplaceAutoSegmentsAsync_CreditsOverlapGuard(
        double introStart,
        double introEnd,
        double creditsStart,
        double creditsEnd,
        bool isUserProvided,
        int expectedCount)
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Introduction,
                [new Segment(itemId, new TimeRange(introStart, introEnd))],
                SegmentSource.Chromaprint);

            if (isUserProvided)
            {
                await database.AddUserSegmentAsync(itemId, AnalysisMode.Credits, Ticks(creditsStart), Ticks(creditsEnd));
            }
            else
            {
                await database.ReplaceAutoSegmentsAsync(
                    itemId,
                    AnalysisMode.Credits,
                    [new Segment(itemId, new TimeRange(creditsStart, creditsEnd))],
                    SegmentSource.BlackFrame);
            }

            var stored = await database.GetSegmentsAsync(itemId);
            Assert.Equal(expectedCount, stored.Count(s => s.Type == AnalysisMode.Credits));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public void LegacyTimestampMapper_SelectsEarliestStartPerMode_ActiveOnly()
    {
        var itemId = Guid.NewGuid();
        var suppressed = new DbSegment(itemId, AnalysisMode.Commercial, Ticks(1), Ticks(4), SegmentSource.Chapter)
        {
            State = SegmentState.Suppressed
        };
        var rows = new[]
        {
            new DbSegment(itemId, AnalysisMode.Commercial, Ticks(50), Ticks(60), SegmentSource.Chapter),
            new DbSegment(itemId, AnalysisMode.Commercial, Ticks(20), Ticks(30), SegmentSource.Chapter),
            new DbSegment(itemId, AnalysisMode.Commercial, Ticks(100), Ticks(110), SegmentSource.Chapter),
            new DbSegment(itemId, AnalysisMode.Introduction, Ticks(5), Ticks(40), SegmentSource.Chromaprint),
            suppressed
        };

        var timestamps = LegacyTimestampMapper.ToCanonical(rows);

        // One representative per mode; for modes with several rows the earliest-start
        // ACTIVE segment wins — the suppressed 1–4 s row must not resurface.
        Assert.Equal(2, timestamps.Count);
        Assert.Equal(20, timestamps[AnalysisMode.Commercial].Start);
        Assert.Equal(30, timestamps[AnalysisMode.Commercial].End);
        Assert.Equal(5, timestamps[AnalysisMode.Introduction].Start);
        Assert.Equal(40, timestamps[AnalysisMode.Introduction].End);
    }

    [Fact]
    public async Task DeleteSegmentAsync_RemovesOnlyTheAddressedSegment()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Commercial,
                [new Segment(itemId, new TimeRange(10, 20)), new Segment(itemId, new TimeRange(30, 40))],
                SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(0, 5))], SegmentSource.Chapter);

            var target = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.StartTicks == Ticks(10));
            var snapshot = await database.DeleteSegmentAsync(itemId, target.Id);

            Assert.NotNull(snapshot);
            Assert.Equal(target.Id, snapshot!.Id);

            // The other commercial and the intro are untouched; the tombstone is hidden
            // from default reads but still stored.
            var active = await database.GetSegmentsAsync(itemId);
            Assert.Equal(2, active.Count);
            var all = await database.GetSegmentsAsync(itemId, includeSuppressed: true);
            Assert.Equal(3, all.Count);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task UpdateSegmentAsync_MovesBoundaries_AndMergesIntoExactOccupant()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Commercial,
                [new Segment(itemId, new TimeRange(10, 20)), new Segment(itemId, new TimeRange(30, 40))],
                SegmentSource.Chapter);

            var target = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.StartTicks == Ticks(10));
            var sibling = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.StartTicks == Ticks(30));

            var updated = await database.UpdateSegmentAsync(itemId, target.Id, Ticks(12), Ticks(22));
            Assert.NotNull(updated);
            Assert.Equal(target.Id, updated!.Id);
            Assert.Equal(SegmentSource.User, updated.Source);
            Assert.Equal(Ticks(12), updated.StartTicks);

            // Moving onto the exact range of the active sibling merges into it: the
            // occupant survives as the user segment (keeping its id) and the moved row
            // is absorbed, mirroring AddUserSegmentAsync's in-place promotion.
            var merged = await database.UpdateSegmentAsync(itemId, target.Id, Ticks(30), Ticks(40));
            Assert.NotNull(merged);
            Assert.Equal(sibling.Id, merged!.Id);
            Assert.Equal(SegmentSource.User, merged.Source);
            var survivor = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
            Assert.Equal(sibling.Id, survivor.Id);

            Assert.Null(await database.UpdateSegmentAsync(itemId, Guid.NewGuid(), Ticks(1), Ticks(2)));
            // The survivor addressed through the wrong item is unknown by contract.
            Assert.Null(await database.UpdateSegmentAsync(Guid.NewGuid(), sibling.Id, Ticks(50), Ticks(60)));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteSegmentsByModeAsync_RemovesOnlyTheGivenMode_IncludingTombstones()
    {
        var dbPath = CreateTempDbPath();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(itemA, AnalysisMode.Introduction, [new Segment(itemA, new TimeRange(0, 30))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(itemA, AnalysisMode.Credits, [new Segment(itemA, new TimeRange(1200, 1260))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(itemB, AnalysisMode.Introduction, [new Segment(itemB, new TimeRange(0, 20))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(itemB, AnalysisMode.Commercial, [new Segment(itemB, new TimeRange(100, 110))], SegmentSource.Chapter);

            // Tombstone one intro so the erase provably clears tombstones too.
            var tombstoned = Assert.Single(await database.GetSegmentsAsync(itemB), s => s.Type == AnalysisMode.Introduction);
            await database.DeleteSegmentAsync(itemB, tombstoned.Id);

            await database.DeleteSegmentsByModeAsync(AnalysisMode.Introduction);

            await using var db = new IntroSkipperDbContext(dbPath);
            var remaining = await db.Segments.AsNoTracking().ToListAsync();
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
            await database.ReplaceAutoSegmentsAsync(targetItemId, AnalysisMode.Introduction, [new Segment(targetItemId, new TimeRange(0, 30))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(targetItemId, AnalysisMode.Credits, [new Segment(targetItemId, new TimeRange(1200, 1260))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(targetItemId, AnalysisMode.Commercial, [new Segment(targetItemId, new TimeRange(100, 110))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(otherItemId, AnalysisMode.Introduction, [new Segment(otherItemId, new TimeRange(0, 20))], SegmentSource.Chapter);

            await database.DeleteItemSegmentsAsync(targetItemId);

            await using var db = new IntroSkipperDbContext(dbPath);
            var remaining = Assert.Single(await db.Segments.AsNoTracking().ToListAsync());
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
            await database.ReplaceAutoSegmentsAsync(targetItemId, AnalysisMode.Introduction, [new Segment(targetItemId, new TimeRange(0, 30))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(otherItemId, AnalysisMode.Introduction, [new Segment(otherItemId, new TimeRange(0, 20))], SegmentSource.Chapter);
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, [targetItemId]);
            await database.SetEpisodeIdsAsync(otherSeasonId, AnalysisMode.Introduction, [otherItemId]);

            // Tombstone the target item's intro: an explicit season erase is a factory
            // reset, so the tombstone must go too.
            var tombstoned = Assert.Single(await database.GetSegmentsAsync(targetItemId));
            await database.DeleteSegmentAsync(targetItemId, tombstoned.Id);

            await database.ClearSeasonAnalysisAsync(seasonId, [targetItemId]);

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.False(await db.Segments.AnyAsync(s => s.ItemId == targetItemId));
            Assert.True(await db.Segments.AnyAsync(s => s.ItemId == otherItemId));
            var targetState = await db.SeasonStates.SingleAsync(s => s.SeasonId == seasonId);
            Assert.Empty(targetState.EpisodeIds);
            var otherState = await db.SeasonStates.SingleAsync(s => s.SeasonId == otherSeasonId);
            Assert.Equal([otherItemId], otherState.EpisodeIds);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task SetEpisodeIdsAsync_AcceptsLazyEnumerable_OnInsertAndUpdate()
    {
        // Regression: EF Core maps the IEnumerable<Guid> EpisodeIds property as a primitive
        // collection whose change tracking only accepts arrays or IList<Guid>. The analyzer
        // task passes items.Select(i => i.EpisodeId) — a lazy iterator — which made both the
        // insert and the update path throw InvalidOperationException until the facade
        // materialized the sequence.
        var dbPath = CreateTempDbPath();
        var seasonId = Guid.NewGuid();
        var retainedEpisodeId = Guid.NewGuid();
        var removedEpisodeId = Guid.NewGuid();
        var episodeIds = new List<Guid> { retainedEpisodeId, removedEpisodeId };
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // Insert path with a lazy projection, mirroring BaseItemAnalyzerTask.
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, episodeIds.Select(id => id), "hash-1");

            // Update path with a lazy projection over the existing row.
            await database.SetEpisodeIdsAsync(
                seasonId,
                AnalysisMode.Introduction,
                episodeIds.Where(id => id != removedEpisodeId),
                "hash-2");

            await using var db = new IntroSkipperDbContext(dbPath);
            var state = await db.SeasonStates
                .AsNoTracking()
                .SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
            Assert.Equal([retainedEpisodeId], state.EpisodeIds);
            Assert.Equal("hash-2", state.ConfigHash);
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
    public async Task GetStaleTimestampEpisodeIdsAsync_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
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
            await database.ReplaceAutoSegmentsAsync(retainedItemId, AnalysisMode.Introduction, [new Segment(retainedItemId, new TimeRange(0, 10))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(staleItemId, AnalysisMode.Introduction, [new Segment(staleItemId, new TimeRange(20, 30))], SegmentSource.Chapter);

            var staleEpisodeIds = await database.GetStaleTimestampEpisodeIdsAsync(enabledEpisodeIds);

            Assert.Equal([staleItemId], staleEpisodeIds);
            var retained = Assert.Single(await database.GetSegmentsAsync(retainedItemId));
            Assert.Equal(retainedItemId, retained.ItemId);
            Assert.Single(await database.GetSegmentsAsync(staleItemId));
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
            await database.ReplaceAutoSegmentsAsync(targetItemId, AnalysisMode.Introduction, [new Segment(targetItemId, new TimeRange(0, 10))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(retainedItemId, AnalysisMode.Introduction, [new Segment(retainedItemId, new TimeRange(20, 30))], SegmentSource.Chapter);

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
    public async Task ReplaceAutoSegmentsAsync_StoresMultipleSegments_AndDeduplicatesExactRanges()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Commercial,
                [
                    new Segment(itemId, new TimeRange(0, 10)),
                    new Segment(itemId, new TimeRange(20, 30)),
                    new Segment(itemId, new TimeRange(20, 30))
                ],
                SegmentSource.Chapter);

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
            var retainedState = await db.SeasonStates
                .AsNoTracking()
                .SingleAsync(s => s.SeasonId == retainedSeasonId);
            Assert.Equal([retainedEpisodeId], retainedState.EpisodeIds);
            Assert.False(await db.SeasonStates.AnyAsync(s => s.SeasonId == staleSeasonId));
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
            await database.ReplaceAutoSegmentsAsync(episodeWithSegmentId, AnalysisMode.Introduction, [new Segment(episodeWithSegmentId, new TimeRange(0, 30))], SegmentSource.Chapter);
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
            await database.ReplaceAutoSegmentsAsync(automaticEpisodeId, AnalysisMode.Introduction, [new Segment(automaticEpisodeId, new TimeRange(0, 30))], SegmentSource.Chapter);
            await database.AddUserSegmentAsync(userProvidedEpisodeId, AnalysisMode.Introduction, Ticks(0), Ticks(30));
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, episodeIds);

            await database.ResetSeasonForReanalysisAsync(seasonId, episodeIds, [AnalysisMode.Introduction]);

            Assert.Empty(await database.GetSegmentsAsync(automaticEpisodeId));
            Assert.Single(await database.GetSegmentsAsync(userProvidedEpisodeId));
            await using var db = new IntroSkipperDbContext(dbPath);
            var seasonState = await db.SeasonStates
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
        var tombstonedItemId = Guid.NewGuid();
        var itemIds = Enumerable.Range(0, LargeItemCount - 5)
            .Select(_ => Guid.NewGuid())
            .Append(staleItemId)
            .Append(userProvidedItemId)
            .Append(matchingHashItemId)
            .Append(otherModeItemId)
            .Append(tombstonedItemId)
            .ToArray();

        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // All retention classes: only the stale-hash ACTIVE automatic segment of the
            // cleaned mode may be deleted.
            await database.ReplaceAutoSegmentsAsync(staleItemId, AnalysisMode.Introduction, [new Segment(staleItemId, new TimeRange(0, 30))], SegmentSource.Chapter, "old-config");
            await database.AddUserSegmentAsync(userProvidedItemId, AnalysisMode.Introduction, Ticks(0), Ticks(30));
            await database.ReplaceAutoSegmentsAsync(matchingHashItemId, AnalysisMode.Introduction, [new Segment(matchingHashItemId, new TimeRange(0, 30))], SegmentSource.Chapter, "new-config");
            await database.ReplaceAutoSegmentsAsync(otherModeItemId, AnalysisMode.Credits, [new Segment(otherModeItemId, new TimeRange(1200, 1260))], SegmentSource.Chapter, "old-config");

            // A tombstoned stale-hash automatic segment must SURVIVE the cleanup —
            // it records user intent, not analysis output.
            await database.ReplaceAutoSegmentsAsync(tombstonedItemId, AnalysisMode.Introduction, [new Segment(tombstonedItemId, new TimeRange(0, 30))], SegmentSource.Chapter, "old-config");
            var tombstoned = Assert.Single(await database.GetSegmentsAsync(tombstonedItemId));
            await database.DeleteSegmentAsync(tombstonedItemId, tombstoned.Id);

            await database.CleanStaleAutomaticSegmentsAsync(itemIds, AnalysisMode.Introduction, "new-config");

            Assert.Empty(await database.GetSegmentsAsync(staleItemId));

            await using var db = new IntroSkipperDbContext(dbPath);
            var remaining = await db.Segments.AsNoTracking().ToListAsync();
            Assert.Equal(4, remaining.Count);
            Assert.Single(remaining, s => s.ItemId == userProvidedItemId && s.Type == AnalysisMode.Introduction && s.Source == SegmentSource.User);
            Assert.Single(remaining, s => s.ItemId == matchingHashItemId && s.Type == AnalysisMode.Introduction && s.ConfigHash == "new-config");
            Assert.Single(remaining, s => s.ItemId == otherModeItemId && s.Type == AnalysisMode.Credits);
            Assert.Single(remaining, s => s.ItemId == tombstonedItemId && s.State == SegmentState.Suppressed);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task RebuildDatabaseAsync_PreservesValidSegmentsSeasonStatesAndImportMarker()
    {
        var dbPath = CreateTempDbPath();
        var automaticItemId = Guid.NewGuid();
        var userProvidedItemId = Guid.NewGuid();
        var tombstonedItemId = Guid.NewGuid();
        var invalidItemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(automaticItemId, AnalysisMode.Introduction, [new Segment(automaticItemId, new TimeRange(5, 30))], SegmentSource.Chromaprint, "cfg-auto");
            await database.ReplaceAutoSegmentsAsync(automaticItemId, AnalysisMode.Commercial, [new Segment(automaticItemId, new TimeRange(100, 110))], SegmentSource.Chapter);
            await database.AddUserSegmentAsync(userProvidedItemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, episodeIds, "cfg-season");
            await database.SetAnalyzerActionAsync(
                seasonId,
                new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Credits] = AnalyzerAction.None });

            // Tombstones record user intent and must survive corruption recovery.
            await database.ReplaceAutoSegmentsAsync(tombstonedItemId, AnalysisMode.Introduction, [new Segment(tombstonedItemId, new TimeRange(0, 30))], SegmentSource.Chapter);
            var tombstoned = Assert.Single(await database.GetSegmentsAsync(tombstonedItemId));
            await database.DeleteSegmentAsync(tombstonedItemId, tombstoned.Id);

            // An invalid segment (EndTicks <= StartTicks) is seeded raw — the facade
            // write paths never produce one — and must be dropped by the rebuild's
            // backup filter.
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO "Segments" ("Id", "ItemId", "Type", "StartTicks", "EndTicks", "Source", "State", "ConfigHash", "CreatedAt", "UpdatedAt")
                    VALUES ($id, $itemId, $type, $start, $end, 1, 0, '', '2026-01-01 00:00:00', '2026-01-01 00:00:00')
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$itemId", invalidItemId.ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$type", (int)AnalysisMode.Introduction);
                command.Parameters.AddWithValue("$start", Ticks(10));
                command.Parameters.AddWithValue("$end", 0L);
                await command.ExecuteNonQueryAsync();
            }

            await database.RebuildDatabaseAsync();

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            var segments = await db.Segments.AsNoTracking().ToListAsync();
            Assert.Equal(4, segments.Count);
            Assert.DoesNotContain(segments, s => s.ItemId == invalidItemId);
            var automatic = Assert.Single(segments, s => s.ItemId == automaticItemId && s.Type == AnalysisMode.Introduction);
            Assert.Equal("cfg-auto", automatic.ConfigHash);
            Assert.False(automatic.IsUserProvided);
            var userProvided = Assert.Single(segments, s => s.Type == AnalysisMode.Credits);
            Assert.Equal(userProvidedItemId, userProvided.ItemId);
            Assert.True(userProvided.IsUserProvided);
            Assert.Single(segments, s => s.Type == AnalysisMode.Commercial);
            Assert.Single(segments, s => s.ItemId == tombstonedItemId && s.State == SegmentState.Suppressed);

            var seasonStates = await db.SeasonStates.AsNoTracking().Where(s => s.SeasonId == seasonId).ToListAsync();
            Assert.Equal(2, seasonStates.Count);
            var introState = Assert.Single(seasonStates, s => s.Type == AnalysisMode.Introduction);
            Assert.Equal(episodeIds, introState.EpisodeIds);
            Assert.Equal("cfg-season", introState.ConfigHash);
            var creditsState = Assert.Single(seasonStates, s => s.Type == AnalysisMode.Credits);
            Assert.Equal(AnalyzerAction.None, creditsState.Action);

            // The import marker survives the rebuild, so the next initialization never
            // re-runs the legacy import on top of the restored rows.
            Assert.True(await db.ImportHistory.AnyAsync());
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
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(5, 30))], SegmentSource.Chapter);

            // Deterministic backup failure: dropping a column the model SELECT expects
            // makes the backup read throw SqliteException ("no such column").
            await DropSegmentsConfigHashColumnAsync(dbPath);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => database.RebuildDatabaseAsync());
            Assert.IsType<SqliteException>(exception.InnerException);

            // The aborted rebuild must not have touched the database file: the seeded
            // row is still there. (Raw connection — the EF model no longer matches.)
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM \"Segments\"";
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
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(5, 30))], SegmentSource.Chapter);

            await DropSegmentsConfigHashColumnAsync(dbPath);

            await database.RebuildDatabaseAsync(forceCleanOnBackupFailure: true);

            // Explicitly requested clean rebuild: the unreadable data is gone and the
            // schema is recreated at the current migration level. The rebuild synthesizes
            // an import marker so the legacy import never runs on top of a rebuilt file.
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.Empty(await db.Segments.AsNoTracking().ToListAsync());
                Assert.Empty(await db.SeasonStates.AsNoTracking().ToListAsync());
                var marker = Assert.Single(await db.ImportHistory.AsNoTracking().ToListAsync());
                Assert.Equal("rebuild", marker.Notes);
            }

            // The facade stays operational over the recreated database.
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(0, 10))], SegmentSource.Chapter);
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
            NullLogger<IntroSkipperDatabase>.Instance);

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
    public void CacheOperation_InitializationFailure_ReturnsNeutralAndNextOperationRetries()
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
            NullLogger<DetectionCacheDatabase>.Instance);

        try
        {
            Assert.Null(database.FindEntry(
                Guid.NewGuid(), AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));

            Assert.Equal(1, Volatile.Read(ref contextCreations));

            Directory.CreateDirectory(directory);

            Assert.Null(database.FindEntry(
                Guid.NewGuid(), AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));
            Assert.Equal(3, Volatile.Read(ref contextCreations)); // Retry context + query context.

            Assert.True(database.TryInitialize());
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
    public async Task CacheOperations_InitializationFailure_ReturnNeutralResults()
    {
        var contextCreations = 0;
        var database = new DetectionCacheDatabase(
            new TestDbContextFactory<DetectionCacheDbContext>(() =>
            {
                contextCreations++;
                throw new IOException("Simulated unavailable cache database.");
            }),
            NullLogger<DetectionCacheDatabase>.Instance);
        var itemId = Guid.NewGuid();

        Assert.Null(database.FindEntry(
            itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));

        database.Upsert(
            itemId,
            AnalysisMode.Introduction,
            CacheEntryType.Chromaprint,
            0,
            30,
            [],
            string.Empty);

        Assert.False(database.HasEntry(
            itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30, string.Empty));
        Assert.Equal(0, database.DeleteForItem(itemId));
        Assert.Equal(0, database.DeleteByMode(AnalysisMode.Introduction));
        Assert.Empty(await database.GetStaleItemIdsAsync(new HashSet<Guid>()));
        Assert.Equal(0, await database.DeleteForItemsAsync([itemId]));

        // Each operation retried initialization, and none created a query context.
        Assert.Equal(7, contextCreations);
    }

    [Fact]
    public async Task RetryableInitializationGate_FailedAttemptIsSharedAndNextAttemptRetries()
    {
        var attempts = 0;
        var gate = new RetryableInitializationGate<Task>(() =>
            ++attempts == 1
                ? Task.FromException(new IOException("Simulated transient database failure."))
                : Task.CompletedTask);

        var failedAttempt = gate.GetAttempt();
        Assert.Same(failedAttempt, gate.GetAttempt());

        var failedTask = failedAttempt.Value;
        Assert.Same(failedTask, gate.GetAttempt().Value);
        await Assert.ThrowsAsync<IOException>(() => failedTask);
        Assert.Equal(1, attempts);

        Assert.True(gate.ResetIfCurrent(failedAttempt));
        Assert.False(gate.ResetIfCurrent(failedAttempt));

        var retryAttempt = gate.GetAttempt();
        Assert.NotSame(failedAttempt, retryAttempt);
        await retryAttempt.Value;
        Assert.Equal(2, attempts);
        Assert.Same(retryAttempt, gate.GetAttempt());
    }

    [Fact]
    public async Task ConcurrentFacadeInstances_InitializeFreshDatabaseWithoutErrors()
    {
        // Tests construct sibling facade instances with independent retryable gates over
        // the same file, so both may run migrations + the legacy-import check
        // concurrently on first use. MigrateAsync takes EF's migration lock and the
        // import commits atomically with its marker; this pins that both succeed.
        var dbPath = CreateTempDbPath();
        var episodeId = Guid.NewGuid();

        try
        {
            var facadeA = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var facadeB = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // Trigger both initialization gates concurrently via first operations. A
            // start barrier ensures both workers are running and parked before either
            // touches its facade, so the two gates genuinely overlap instead of racing
            // opportunistically.
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
            Assert.All(results, segments => Assert.Empty(segments));

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            // Both gates may have answered the (no-legacy-file) import question before
            // either marker was visible; one or two markers are both fine — the point is
            // that neither initialization failed.
            Assert.True(await db.ImportHistory.CountAsync() >= 1);
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
            Assert.True(cacheDatabase.TryInitialize());

            Assert.Equal("wal", GetJournalMode(dbPath));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CacheDeletes_DatabaseErrorAfterInitialization_IsSwallowedAndReturnsZero()
    {
        // First context (initialization) uses a working path; every later context points
        // into a nonexistent directory, so the delete statements themselves fail with a
        // SqliteException. The facade's deletes are best-effort and must swallow it.
        var goodPath = CreateTempDbPath();
        var badPath = Path.Join(
            Path.GetTempPath(),
            "IntroSkipper.Tests",
            Guid.NewGuid().ToString("N") + "-missing-dir",
            "cache.db");
        var contextCreations = 0;
        var database = new DetectionCacheDatabase(
            new TestDbContextFactory<DetectionCacheDbContext>(() =>
                new DetectionCacheDbContext(Interlocked.Increment(ref contextCreations) == 1 ? goodPath : badPath)),
            NullLogger<DetectionCacheDatabase>.Instance);

        try
        {
            Assert.True(database.TryInitialize());

            Assert.Equal(0, database.DeleteForItem(Guid.NewGuid()));
            Assert.Equal(0, database.DeleteByMode(AnalysisMode.Introduction));
            Assert.Equal(0, await database.DeleteForItemsAsync([Guid.NewGuid()]));
        }
        finally
        {
            DeleteSqliteFiles(goodPath);
        }
    }

    private static async Task DropSegmentsConfigHashColumnAsync(string dbPath)
    {
        // Clear pooled handles first so the schema change cannot hit a stale lock.
        SqliteConnection.ClearAllPools();

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE \"Segments\" DROP COLUMN \"ConfigHash\"";
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
        => DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-facades.db");

    private static void DeleteSqliteFiles(string dbPath)
        => DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
}
