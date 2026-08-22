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
    private static Dictionary<AnalysisMode, (long StartTicks, long EndTicks)> Slot(AnalysisMode mode, long startTicks, long endTicks)
        => new() { [mode] = (startTicks, endTicks) };

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
    public async Task SegmentWrites_RejectUndefinedMode_WithoutPersistingAnything()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var undefined = (AnalysisMode)999;

            // A persisted undefined mode would poison every later conversion of the
            // item (ModeToSegmentType indexing throws), so the facade rejects it at
            // every write that stamps a Type.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => database.AddUserSegmentAsync(itemId, undefined, Ticks(10), Ticks(20)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => database.ReplaceUserSegmentsAsync(itemId, Slot(undefined, Ticks(10), Ticks(20))));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => database.ReplaceAutoSegmentsAsync(
                    itemId, undefined, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter));

            Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
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
            Assert.Equal(SegmentSource.User, stored.Source);
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
            Assert.Single(stored, s => s.Source == SegmentSource.User);
            Assert.Single(stored, s => s.Source == SegmentSource.Chapter && s.StartTicks == Ticks(autoStart));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ReplaceUserSegmentsAsync_ReplacesAnalysisResult()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(20, 80))], SegmentSource.Chromaprint);
            await database.ReplaceUserSegmentsAsync(itemId, Slot(AnalysisMode.Introduction, Ticks(10), Ticks(60)));

            var stored = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.Equal(SegmentSource.User, stored.Source);
            Assert.Equal(Ticks(10), stored.StartTicks);
            Assert.Equal(Ticks(60), stored.EndTicks);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ReplaceUserSegmentsAsync_PerMode_PromotesExactRangeInPlace_AndLeavesAbsentModes()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Introduction,
                [new Segment(itemId, new TimeRange(10, 60)), new Segment(itemId, new TimeRange(300, 330))],
                SegmentSource.Chromaprint,
                configHash: "analyzer-hash");
            await database.ReplaceAutoSegmentsAsync(
                itemId,
                AnalysisMode.Credits,
                [new Segment(itemId, new TimeRange(1200, 1260))],
                SegmentSource.Chromaprint,
                configHash: "analyzer-hash");
            var autoIntro = Assert.Single(await database.GetSegmentsAsync(itemId), s => s.Type == AnalysisMode.Introduction && s.StartTicks == Ticks(10));

            await database.ReplaceUserSegmentsAsync(
                itemId,
                new Dictionary<AnalysisMode, (long StartTicks, long EndTicks)>
                {
                    [AnalysisMode.Introduction] = (Ticks(10), Ticks(60)),
                    [AnalysisMode.Commercial] = (Ticks(400), Ticks(430))
                });

            // The exact-range occupant keeps its id (Jellyfin addresses the row by it) and
            // changes hands, every other active intro is gone, the commercial slot is a
            // new user row, and the credits mode was not named so it is untouched.
            var stored = await database.GetSegmentsAsync(itemId, includeSuppressed: true);
            var intro = Assert.Single(stored, s => s.Type == AnalysisMode.Introduction);
            Assert.Equal(autoIntro.Id, intro.Id);
            Assert.Equal(SegmentSource.User, intro.Source);
            Assert.Empty(intro.ConfigHash);
            var commercial = Assert.Single(stored, s => s.Type == AnalysisMode.Commercial);
            Assert.Equal(SegmentSource.User, commercial.Source);
            Assert.Equal(Ticks(400), commercial.StartTicks);
            var credits = Assert.Single(stored, s => s.Type == AnalysisMode.Credits);
            Assert.Equal(SegmentSource.Chromaprint, credits.Source);
            Assert.Equal("analyzer-hash", credits.ConfigHash);
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

            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chapter, configHash: "analyzer-hash");
            var autoRow = Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.Equal("analyzer-hash", autoRow.ConfigHash);

            var promoted = await database.AddUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(10), Ticks(60));

            // Same row, same id — the auto row was promoted instead of duplicated.
            Assert.Equal(autoRow.Id, promoted.Id);
            Assert.Equal(SegmentSource.User, promoted.Source);
            // Provenance moves with the hash: a user row carries no analyzer config hash.
            Assert.Empty(promoted.ConfigHash);
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

            // Analysis records: the erased mode's must go so the next scan re-detects;
            // other modes' stay.
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [itemA, itemB], "intro-hash");
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [itemA], "credits-hash");

            var erasedItemIds = await database.DeleteSegmentsByModeAsync(AnalysisMode.Introduction);

            Assert.Equal(2, erasedItemIds.Count);
            Assert.Contains(itemA, erasedItemIds);
            Assert.Contains(itemB, erasedItemIds);

            await using var db = new IntroSkipperDbContext(dbPath);
            var remaining = await db.Segments.AsNoTracking().ToListAsync();
            Assert.Equal(2, remaining.Count);
            Assert.DoesNotContain(remaining, s => s.Type == AnalysisMode.Introduction);
            Assert.Single(remaining, s => s.ItemId == itemA && s.Type == AnalysisMode.Credits);
            Assert.Single(remaining, s => s.ItemId == itemB && s.Type == AnalysisMode.Commercial);

            var record = Assert.Single(await db.AnalyzedItems.AsNoTracking().ToListAsync());
            Assert.Equal((itemA, AnalysisMode.Credits), (record.ItemId, record.Type));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task CleanStaleAutomaticSegmentsAsync_CreditsDerivedPreview_IsJudgedByTheCreditsPass()
    {
        var dbPath = CreateTempDbPath();
        var itemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            // The credits pass stamps derived previews with the credits hash; a chapter
            // preview on another item carries the preview hash like any automatic row.
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Preview, [new Segment(itemId, new TimeRange(1380, 1440))], SegmentSource.CreditsDerived, configHash: "credits-hash");
            var otherItemId = Guid.NewGuid();
            await database.ReplaceAutoSegmentsAsync(otherItemId, AnalysisMode.Preview, [new Segment(otherItemId, new TimeRange(1300, 1440))], SegmentSource.Chapter, configHash: "stale-preview-hash");

            // The preview pass ignores the derived row even though its hash differs.
            await database.CleanStaleAutomaticSegmentsAsync([itemId, otherItemId], AnalysisMode.Preview, "preview-hash");
            Assert.Single(await database.GetSegmentsAsync(itemId));
            Assert.Empty(await database.GetSegmentsAsync(otherItemId));

            // The credits pass keeps it while the credits hash matches ...
            await database.CleanStaleAutomaticSegmentsAsync([itemId], AnalysisMode.Credits, "credits-hash");
            Assert.Single(await database.GetSegmentsAsync(itemId));

            // ... and drops it once the credits configuration changed.
            await database.CleanStaleAutomaticSegmentsAsync([itemId], AnalysisMode.Credits, "new-credits-hash");
            Assert.Empty(await database.GetSegmentsAsync(itemId));
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
    public async Task EraseItemsAsync_DeletesSegmentsAndAnalysisRecords_OfTheGivenItemsOnly()
    {
        var dbPath = CreateTempDbPath();
        var targetItemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(targetItemId, AnalysisMode.Introduction, [new Segment(targetItemId, new TimeRange(0, 30))], SegmentSource.Chapter);
            await database.ReplaceAutoSegmentsAsync(otherItemId, AnalysisMode.Introduction, [new Segment(otherItemId, new TimeRange(0, 20))], SegmentSource.Chapter);
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [targetItemId, otherItemId], "hash");

            // Tombstone the target item's intro and add a user row: an explicit erase is a
            // factory reset, so the tombstone and the user row go too (and are counted).
            var tombstoned = Assert.Single(await database.GetSegmentsAsync(targetItemId));
            await database.DeleteSegmentAsync(targetItemId, tombstoned.Id);
            await database.AddUserSegmentAsync(targetItemId, AnalysisMode.Introduction, Ticks(100), Ticks(130));

            var removed = await database.EraseItemsAsync([targetItemId]);

            Assert.Equal(2, removed);
            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.False(await db.Segments.AnyAsync(s => s.ItemId == targetItemId));
            Assert.False(await db.AnalyzedItems.AnyAsync(a => a.ItemId == targetItemId));
            Assert.True(await db.Segments.AnyAsync(s => s.ItemId == otherItemId));
            Assert.True(await db.AnalyzedItems.AnyAsync(a => a.ItemId == otherItemId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task MarkItemsAnalyzedAsync_ReplacesTheRecordPerItem_AndAcceptsLazyEnumerables()
    {
        var dbPath = CreateTempDbPath();
        var retainedEpisodeId = Guid.NewGuid();
        var rehashedEpisodeId = Guid.NewGuid();
        var episodeIds = new List<Guid> { retainedEpisodeId, rehashedEpisodeId };
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // Lazy projections mirror BaseItemAnalyzerTask's items.Select(i => i.EpisodeId).
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, episodeIds.Select(id => id), "hash-1");
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, episodeIds.Where(id => id == rehashedEpisodeId), "hash-2");

            // A later pass overwrites only the items it covered; the rest keep their record.
            await using var db = new IntroSkipperDbContext(dbPath);
            var records = await db.AnalyzedItems.AsNoTracking().ToDictionaryAsync(a => a.ItemId, a => a.ConfigHash);
            Assert.Equal(2, records.Count);
            Assert.Equal("hash-1", records[retainedEpisodeId]);
            Assert.Equal("hash-2", records[rehashedEpisodeId]);
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
        var retainedSeasonIds = Enumerable.Range(0, LargeSeasonCount - 1)
            .Select(_ => Guid.NewGuid())
            .Append(retainedSeasonId)
            .ToArray();

        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var action = new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Chapter };
            await database.SetAnalyzerActionAsync(retainedSeasonId, action);
            await database.SetAnalyzerActionAsync(staleSeasonId, action);

            await database.CleanSeasonStateAsync(retainedSeasonIds);

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.True(await db.SeasonStates.AnyAsync(s => s.SeasonId == retainedSeasonId));
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
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [episodeWithSegmentId], "snapshot-config");

            var snapshot = await database.GetSeasonQueueSnapshotAsync(seasonId, episodeIds);

            Assert.Equal("snapshot-config", snapshot.AnalyzedConfigHashes[(episodeWithSegmentId, AnalysisMode.Introduction)]);
            Assert.True(snapshot.SegmentsByEpisodeId.TryGetValue(episodeWithSegmentId, out var segmentsByAnalysisMode));
            Assert.True(segmentsByAnalysisMode!.TryGetValue(AnalysisMode.Introduction, out _));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ResetItemsForReanalysisAsync_DoesNotExceedSqliteVariableLimit_WhenEpisodeListIsLarge()
    {
        const int LargeEpisodeCount = 33_000;

        var dbPath = CreateTempDbPath();
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
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, episodeIds, "hash");

            await database.ResetItemsForReanalysisAsync(episodeIds, [AnalysisMode.Introduction]);

            Assert.Empty(await database.GetSegmentsAsync(automaticEpisodeId));
            Assert.Single(await database.GetSegmentsAsync(userProvidedEpisodeId));
            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.False(await db.AnalyzedItems.AnyAsync());
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
    public async Task RebuildDatabaseAsync_PreservesValidSegmentsSeasonStatesAnalysisRecordsAndImportMarker()
    {
        var dbPath = CreateTempDbPath();
        var automaticItemId = Guid.NewGuid();
        var userProvidedItemId = Guid.NewGuid();
        var tombstonedItemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        try
        {
            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.ReplaceAutoSegmentsAsync(automaticItemId, AnalysisMode.Introduction, [new Segment(automaticItemId, new TimeRange(5, 30))], SegmentSource.Chromaprint, "cfg-auto");
            await database.ReplaceAutoSegmentsAsync(automaticItemId, AnalysisMode.Commercial, [new Segment(automaticItemId, new TimeRange(100, 110))], SegmentSource.Chapter);
            await database.AddUserSegmentAsync(userProvidedItemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, episodeIds, "cfg-season");
            await database.SetAnalyzerActionAsync(
                seasonId,
                new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Credits] = AnalyzerAction.None });

            // Tombstones record user intent and must survive corruption recovery.
            await database.ReplaceAutoSegmentsAsync(tombstonedItemId, AnalysisMode.Introduction, [new Segment(tombstonedItemId, new TimeRange(0, 30))], SegmentSource.Chapter);
            var tombstoned = Assert.Single(await database.GetSegmentsAsync(tombstonedItemId));
            await database.DeleteSegmentAsync(tombstonedItemId, tombstoned.Id);

            await database.RebuildDatabaseAsync();

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            var segments = await db.Segments.AsNoTracking().ToListAsync();
            Assert.Equal(4, segments.Count);
            var automatic = Assert.Single(segments, s => s.ItemId == automaticItemId && s.Type == AnalysisMode.Introduction);
            Assert.Equal("cfg-auto", automatic.ConfigHash);
            Assert.NotEqual(SegmentSource.User, automatic.Source);
            var userProvided = Assert.Single(segments, s => s.Type == AnalysisMode.Credits);
            Assert.Equal(userProvidedItemId, userProvided.ItemId);
            Assert.Equal(SegmentSource.User, userProvided.Source);
            Assert.Single(segments, s => s.Type == AnalysisMode.Commercial);
            Assert.Single(segments, s => s.ItemId == tombstonedItemId && s.State == SegmentState.Suppressed);

            var creditsState = Assert.Single(await db.SeasonStates.AsNoTracking().Where(s => s.SeasonId == seasonId).ToListAsync());
            Assert.Equal(AnalysisMode.Credits, creditsState.Type);
            Assert.Equal(AnalyzerAction.None, creditsState.Action);
            var analyzed = await db.AnalyzedItems.AsNoTracking().ToListAsync();
            Assert.Equal(episodeIds.OrderBy(id => id), analyzed.Select(a => a.ItemId).OrderBy(id => id));
            Assert.All(analyzed, a => Assert.Equal("cfg-season", a.ConfigHash));

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
            // schema is recreated at the current migration level. No import marker is
            // synthesized: with the rebuilt file empty, the legacy database may be the
            // only copy of the user's data, so the next start must import it.
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.Empty(await db.Segments.AsNoTracking().ToListAsync());
                Assert.Empty(await db.SeasonStates.AsNoTracking().ToListAsync());
                Assert.Empty(await db.ImportHistory.AsNoTracking().ToListAsync());
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
