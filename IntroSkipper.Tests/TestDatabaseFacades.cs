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
using IntroSkipper.SegmentChanges;
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
public sealed class TestDatabaseFacades : IDisposable
{
    private readonly TempSegmentDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task InitializationGate_CreatesSchemaBeforeFirstQuery()
    {
        // No EnsureCreated, no migrations — the facade's initialization gate must run
        // migrations + the one-time legacy import before the first query touches the
        // database.
        var database = _db.Database;
        var segments = await database.GetSegmentsAsync(Guid.NewGuid());
        Assert.Empty(segments);

        await using var db = _db.Context();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // With no legacy file next to the database, the import question is answered
        // once and recorded, so later restarts never reconsider it.
        var marker = Assert.Single(await db.ImportHistory.AsNoTracking().ToListAsync());
        Assert.False(marker.SourceFileFound);
    }

    [Fact]
    public async Task SegmentWrites_RejectUndefinedMode_WithoutPersistingAnything()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        var undefined = (AnalysisMode)999;

        // A persisted undefined mode would poison every later conversion of the
        // item (ModeToSegmentType indexing throws), so the facade rejects it at
        // every write that stamps a Type.
        Assert.IsType<Rejected>((await database.ApplyChangeAsync(
            new AddUserSegmentIntent(itemId, undefined, Ticks(10), Ticks(20)))).Outcome);
        Assert.IsType<Rejected>((await database.ApplyChangeAsync(
            new WriteUserTimestampsIntent(itemId, [new UserTimestamp(undefined, Ticks(10), Ticks(20))]))).Outcome);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => database.ReplaceAutoSegmentsAsync(
                itemId, undefined, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter));

        Assert.Empty(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
    }

    [Fact]
    public async Task ReplaceAutoSegmentsAsync_DoesNotOverwriteOverlappingUserSegment()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;

        await database.SeedUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(10), Ticks(60));

        var analyzed = new Segment(itemId, new TimeRange(20, 80));
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [analyzed], SegmentSource.Chromaprint);

        var stored = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.User, stored.Source);
        Assert.Equal(Ticks(10), stored.StartTicks);
        Assert.Equal(Ticks(60), stored.EndTicks);
    }

    // The admission gate blocks automatic rows that strictly overlap recorded human
    // intent (a user row or a tombstone); exactly abutting it is not overlap.
    [Theory]
    [InlineData(100.0, 120.0, false)] // disjoint range elsewhere in the episode
    [InlineData(20.0, 30.0, false)]   // starts exactly at the blocker's end
    [InlineData(0.0, 10.0, false)]    // ends exactly at the blocker's start
    [InlineData(100.0, 120.0, true)]
    [InlineData(20.0, 30.0, true)]
    [InlineData(0.0, 10.0, true)]
    public async Task ReplaceAutoSegmentsAsync_AcceptsAutoSegmentNotOverlappingBlocker(double autoStart, double autoEnd, bool blockerIsTombstone)
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;

        if (blockerIsTombstone)
        {
            await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Commercial, [new Segment(itemId, new TimeRange(10, 20))], SegmentSource.Chapter);
            await database.DeleteSegmentAsync(itemId, Assert.Single(await database.GetSegmentsAsync(itemId)).Id);
        }
        else
        {
            await database.SeedUserSegmentAsync(itemId, AnalysisMode.Commercial, Ticks(10), Ticks(20));
        }

        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Commercial,
            [new Segment(itemId, new TimeRange(autoStart, autoEnd))],
            SegmentSource.Chapter);

        var active = await database.GetSegmentsAsync(itemId);
        Assert.Equal(blockerIsTombstone ? 1 : 2, active.Count);
        Assert.Single(active, s => s.Source == SegmentSource.Chapter && s.StartTicks == Ticks(autoStart) && s.EndTicks == Ticks(autoEnd));
        Assert.Equal(!blockerIsTombstone, active.Any(s => s.Source == SegmentSource.User));
    }

    [Fact]
    public async Task WriteUserTimestamps_ReplacesAnalysisResult()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;

        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(20, 80))], SegmentSource.Chromaprint);
        await database.SeedUserTimestampsAsync(itemId, (AnalysisMode.Introduction, Ticks(10), Ticks(60)));

        var stored = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.User, stored.Source);
        Assert.Equal(Ticks(10), stored.StartTicks);
        Assert.Equal(Ticks(60), stored.EndTicks);
    }

    [Fact]
    public async Task WriteUserTimestamps_PerMode_PromotesExactRangeInPlace_AndLeavesAbsentModes()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
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

        await database.SeedUserTimestampsAsync(
            itemId,
            (AnalysisMode.Introduction, Ticks(10), Ticks(60)),
            (AnalysisMode.Commercial, Ticks(400), Ticks(430)));

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

    [Fact]
    public async Task AddUserSegment_PromotesExactMatchingAutoRowInPlace()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;

        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 60))], SegmentSource.Chapter, configHash: "analyzer-hash");
        var autoRow = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal("analyzer-hash", autoRow.ConfigHash);

        var promoted = await database.SeedUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(10), Ticks(60));

        // Same row, same id — the auto row was promoted instead of duplicated.
        Assert.Equal(autoRow.Id, promoted.Id);
        Assert.Equal(SegmentSource.User, promoted.Source);
        // Provenance moves with the hash: a user row carries no analyzer config hash.
        Assert.Empty(promoted.ConfigHash);
        Assert.Single(await database.GetSegmentsAsync(itemId));
    }

    [Fact]
    public async Task ReplaceAutoSegmentsAsync_KeepsIdsOfUnchangedRows()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;

        // The duplicate range in the batch is dropped, not stored twice.
        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Commercial,
            [new Segment(itemId, new TimeRange(10, 20)), new Segment(itemId, new TimeRange(50, 60)), new Segment(itemId, new TimeRange(50, 60))],
            SegmentSource.Chapter,
            "hash-1");
        var before = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, before.Count);
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
        var itemId = Guid.NewGuid();
        var database = _db.Database;

        await database.ReplaceAutoSegmentsAsync(
            itemId,
            AnalysisMode.Introduction,
            [new Segment(itemId, new TimeRange(introStart, introEnd))],
            SegmentSource.Chromaprint);

        if (isUserProvided)
        {
            await database.SeedUserSegmentAsync(itemId, AnalysisMode.Credits, Ticks(creditsStart), Ticks(creditsEnd));
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
    public async Task UpdateSegment_MovesBoundaries_AndMergesIntoExactOccupant()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
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
        // is absorbed, mirroring the add's in-place promotion.
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

    [Fact]
    public async Task DeleteSegmentsByModeAsync_RemovesOnlyTheGivenMode_IncludingTombstones()
    {
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var database = _db.Database;
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

        await using (var db = _db.Context())
        {
            var remaining = await db.Segments.AsNoTracking().ToListAsync();
            Assert.Equal(2, remaining.Count);
            Assert.DoesNotContain(remaining, s => s.Type == AnalysisMode.Introduction);
            Assert.Single(remaining, s => s.ItemId == itemA && s.Type == AnalysisMode.Credits);
            Assert.Single(remaining, s => s.ItemId == itemB && s.Type == AnalysisMode.Commercial);

            var record = Assert.Single(await db.AnalyzedItems.AsNoTracking().ToListAsync());
            Assert.Equal((itemA, AnalysisMode.Credits), (record.ItemId, record.Type));
        }

        // Erase-by-mode is a factory reset: with the tombstone gone, the next
        // analysis run may store the range again.
        await database.ReplaceAutoSegmentsAsync(itemB, AnalysisMode.Introduction, [new Segment(itemB, new TimeRange(0, 20))], SegmentSource.Chapter);
        Assert.Single(await database.GetSegmentsAsync(itemB), s => s.Type == AnalysisMode.Introduction);
    }

    [Fact]
    public async Task CleanStaleAutomaticSegmentsAsync_CreditsDerivedPreview_IsJudgedByTheCreditsPass()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
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

    [Fact]
    public async Task ReplaceAutoSegmentsAsync_PreviewPasses_DoNotDeleteEachOthersRows()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;

        // The credits pass derives a preview, then the preview pass stores a chapter
        // preview with different boundaries. The passes share the Preview mode but
        // own only their own rows, so neither write may delete the other's.
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Preview, [new Segment(itemId, new TimeRange(1380, 1440))], SegmentSource.CreditsDerived, configHash: "credits-hash");
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Preview, [new Segment(itemId, new TimeRange(1350, 1440))], SegmentSource.Chapter, configHash: "preview-hash");

        var segments = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, segments.Count);
        Assert.Contains(segments, s => s.Source == SegmentSource.CreditsDerived);
        Assert.Contains(segments, s => s.Source == SegmentSource.Chapter);

        // A re-derive replaces only the derived row; the chapter row stands.
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Preview, [new Segment(itemId, new TimeRange(1390, 1440))], SegmentSource.CreditsDerived, configHash: "credits-hash");
        segments = await database.GetSegmentsAsync(itemId);
        Assert.Equal(2, segments.Count);
        Assert.Contains(segments, s => s.Source == SegmentSource.CreditsDerived && s.StartTicks == Ticks(1390));
        Assert.Contains(segments, s => s.Source == SegmentSource.Chapter);

        // An empty preview-pass write clears only the pass's own (chapter) row.
        var stored = await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Preview, [], SegmentSource.Chapter, configHash: "preview-hash");
        Assert.Equal(0, stored);
        var remaining = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.CreditsDerived, remaining.Source);

        // A chapter preview landing exactly on the derived range leaves the derived
        // row standing instead of violating the unique quadruple index.
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Preview, [new Segment(itemId, new TimeRange(1390, 1440))], SegmentSource.Chapter, configHash: "preview-hash");
        remaining = Assert.Single(await database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.CreditsDerived, remaining.Source);
    }

    [Fact]
    public async Task EraseItemsAsync_DeletesSegmentsAndAnalysisRecords_OfTheGivenItemsOnly()
    {
        var targetItemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(targetItemId, AnalysisMode.Introduction, [new Segment(targetItemId, new TimeRange(0, 30))], SegmentSource.Chapter);
        await database.ReplaceAutoSegmentsAsync(otherItemId, AnalysisMode.Introduction, [new Segment(otherItemId, new TimeRange(0, 20))], SegmentSource.Chapter);

        // Tombstone the target item's intro and add a user row: an explicit erase is a
        // factory reset, so the tombstone and the user row go too (and are counted).
        // The records come after the delete, which clears the target's on its own.
        var tombstoned = Assert.Single(await database.GetSegmentsAsync(targetItemId));
        await database.DeleteSegmentAsync(targetItemId, tombstoned.Id);
        await database.SeedUserSegmentAsync(targetItemId, AnalysisMode.Introduction, Ticks(100), Ticks(130));
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [targetItemId, otherItemId], "hash");

        var removed = await database.EraseItemsAsync([targetItemId]);

        Assert.Equal(2, removed);
        await using var db = _db.Context();
        Assert.False(await db.Segments.AnyAsync(s => s.ItemId == targetItemId));
        Assert.False(await db.AnalyzedItems.AnyAsync(a => a.ItemId == targetItemId));
        Assert.True(await db.Segments.AnyAsync(s => s.ItemId == otherItemId));
        Assert.True(await db.AnalyzedItems.AnyAsync(a => a.ItemId == otherItemId));
    }

    [Fact]
    public async Task MarkItemsAnalyzedAsync_ReplacesTheRecordPerItem_AndAcceptsLazyEnumerables()
    {
        var retainedEpisodeId = Guid.NewGuid();
        var rehashedEpisodeId = Guid.NewGuid();
        var episodeIds = new List<Guid> { retainedEpisodeId, rehashedEpisodeId };
        var database = _db.Database;

        // Lazy projections mirror BaseItemAnalyzerTask's items.Select(i => i.EpisodeId).
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, episodeIds.Select(id => id), "hash-1");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, episodeIds.Where(id => id == rehashedEpisodeId), "hash-2");

        // A later pass overwrites only the items it covered; the rest keep their record.
        await using var db = _db.Context();
        var records = await db.AnalyzedItems.AsNoTracking().ToDictionaryAsync(a => a.ItemId, a => a.ConfigHash);
        Assert.Equal(2, records.Count);
        Assert.Equal("hash-1", records[retainedEpisodeId]);
        Assert.Equal("hash-2", records[rehashedEpisodeId]);
    }

    [Fact]
    public async Task AnalyzerActions_ReturnStoredRow_AndFillMissingModesWithDefault()
    {
        var seasonId = Guid.NewGuid();
        var database = _db.Database;
        await database.SetAnalyzerActionAsync(
            seasonId,
            new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Chromaprint });

        // A second write updates the stored row in place instead of colliding on the key.
        await database.SetAnalyzerActionAsync(
            seasonId,
            new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Chromaprint });

        var allActions = await database.GetAllAnalyzerActionsAsync(seasonId);
        Assert.Equal(Enum.GetValues<AnalysisMode>().Length, allActions.Count);
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            Assert.Equal(
                mode == AnalysisMode.Introduction ? AnalyzerAction.Chromaprint : AnalyzerAction.Default,
                allActions[mode]);
        }
    }

    public enum LargeIdSetOperation
    {
        StaleTimestampEpisodeIds,
        EraseItems,
        CleanSeasonState,
        SeasonQueueSnapshot,
        ResetItemsForReanalysis,
        CacheStaleIdsAndDelete
    }

    [Theory]
    [InlineData(LargeIdSetOperation.StaleTimestampEpisodeIds)]
    [InlineData(LargeIdSetOperation.EraseItems)]
    [InlineData(LargeIdSetOperation.CleanSeasonState)]
    [InlineData(LargeIdSetOperation.SeasonQueueSnapshot)]
    [InlineData(LargeIdSetOperation.ResetItemsForReanalysis)]
    [InlineData(LargeIdSetOperation.CacheStaleIdsAndDelete)]
    public async Task IdSetOperations_DoNotExceedSqliteVariableLimit_WhenTheSetIsLarge(LargeIdSetOperation operation)
    {
        // EF Core 10 translates parameterized collections on SQLite to discrete padded
        // parameters, and SQLite rejects statements above 32,766 variables, so every
        // id-set operation must bind its set as a single EF.Parameter JSON parameter
        // (json_each). Each case names one kept and one stale id and pads the set past
        // the limit with filler.
        const int LargeCount = 33_000;

        var keptId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        static Guid[] Padded(params Guid[] named) => [.. Enumerable.Range(0, LargeCount - named.Length).Select(_ => Guid.NewGuid()), .. named];

        var database = _db.Database;
        switch (operation)
        {
            case LargeIdSetOperation.StaleTimestampEpisodeIds:
                await database.ReplaceAutoSegmentsAsync(keptId, AnalysisMode.Introduction, [new Segment(keptId, new TimeRange(0, 10))], SegmentSource.Chapter);
                await database.ReplaceAutoSegmentsAsync(staleId, AnalysisMode.Introduction, [new Segment(staleId, new TimeRange(20, 30))], SegmentSource.Chapter);

                Assert.Equal([staleId], await database.GetStaleTimestampEpisodeIdsAsync(Padded(keptId)));
                break;

            case LargeIdSetOperation.EraseItems:
                await database.ReplaceAutoSegmentsAsync(keptId, AnalysisMode.Introduction, [new Segment(keptId, new TimeRange(0, 10))], SegmentSource.Chapter);
                await database.ReplaceAutoSegmentsAsync(staleId, AnalysisMode.Introduction, [new Segment(staleId, new TimeRange(20, 30))], SegmentSource.Chapter);

                Assert.Equal(1, await database.EraseItemsAsync(Padded(staleId)));
                Assert.Empty(await database.GetSegmentsAsync(staleId));
                Assert.Single(await database.GetSegmentsAsync(keptId));
                break;

            case LargeIdSetOperation.CleanSeasonState:
                {
                    var action = new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.Chapter };
                    await database.SetAnalyzerActionAsync(keptId, action);
                    await database.SetAnalyzerActionAsync(staleId, action);

                    await database.CleanSeasonStateAsync(Padded(keptId));

                    await using var db = _db.Context();
                    Assert.True(await db.SeasonStates.AnyAsync(s => s.SeasonId == keptId));
                    Assert.False(await db.SeasonStates.AnyAsync(s => s.SeasonId == staleId));
                    break;
                }

            case LargeIdSetOperation.SeasonQueueSnapshot:
                {
                    await database.ReplaceAutoSegmentsAsync(keptId, AnalysisMode.Introduction, [new Segment(keptId, new TimeRange(0, 30))], SegmentSource.Chapter);
                    await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [keptId], "snapshot-config");

                    var snapshot = await database.GetSeasonQueueSnapshotAsync(Guid.NewGuid(), Padded(keptId));

                    Assert.Equal("snapshot-config", snapshot.AnalyzedConfigHashes[(keptId, AnalysisMode.Introduction)]);
                    Assert.Contains(AnalysisMode.Introduction, snapshot.SegmentModesByEpisodeId[keptId]);
                    break;
                }

            case LargeIdSetOperation.ResetItemsForReanalysis:
                {
                    // The kept item's user row shields it from the reset.
                    var ids = Padded(staleId, keptId);
                    await database.ReplaceAutoSegmentsAsync(staleId, AnalysisMode.Introduction, [new Segment(staleId, new TimeRange(0, 30))], SegmentSource.Chapter);
                    await database.SeedUserSegmentAsync(keptId, AnalysisMode.Introduction, Ticks(0), Ticks(30));
                    await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, ids, "hash");

                    await database.ResetItemsForReanalysisAsync(ids, [AnalysisMode.Introduction]);

                    Assert.Empty(await database.GetSegmentsAsync(staleId));
                    Assert.Single(await database.GetSegmentsAsync(keptId));
                    await using var db = _db.Context();
                    Assert.False(await db.AnalyzedItems.AnyAsync());
                    break;
                }

            case LargeIdSetOperation.CacheStaleIdsAndDelete:
                {
                    var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(_db.Path);
                    cacheDatabase.Upsert(keptId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10, EntrypointTestHelpers.EmptyJsonArray, string.Empty);
                    cacheDatabase.Upsert(staleId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10, EntrypointTestHelpers.EmptyJsonArray, string.Empty);

                    Assert.Equal([staleId], await cacheDatabase.GetStaleItemIdsAsync(Padded(keptId).ToHashSet()));

                    Assert.Equal(1, await cacheDatabase.DeleteForItemsAsync(Padded(staleId)));
                    Assert.Null(cacheDatabase.FindEntry(staleId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10));
                    Assert.NotNull(cacheDatabase.FindEntry(keptId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10));
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    [Fact]
    public async Task CleanStaleAutomaticSegmentsAsync_DeletesOnlyStaleAutomaticSegments_WhenItemListIsLarge()
    {
        const int LargeItemCount = 33_000;

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

        var database = _db.Database;

        // All retention classes: only the stale-hash ACTIVE automatic segment of the
        // cleaned mode may be deleted.
        await database.ReplaceAutoSegmentsAsync(staleItemId, AnalysisMode.Introduction, [new Segment(staleItemId, new TimeRange(0, 30))], SegmentSource.Chapter, "old-config");
        await database.SeedUserSegmentAsync(userProvidedItemId, AnalysisMode.Introduction, Ticks(0), Ticks(30));
        await database.ReplaceAutoSegmentsAsync(matchingHashItemId, AnalysisMode.Introduction, [new Segment(matchingHashItemId, new TimeRange(0, 30))], SegmentSource.Chapter, "new-config");
        await database.ReplaceAutoSegmentsAsync(otherModeItemId, AnalysisMode.Credits, [new Segment(otherModeItemId, new TimeRange(1200, 1260))], SegmentSource.Chapter, "old-config");

        // A tombstoned stale-hash automatic segment must SURVIVE the cleanup —
        // it records user intent, not analysis output.
        await database.ReplaceAutoSegmentsAsync(tombstonedItemId, AnalysisMode.Introduction, [new Segment(tombstonedItemId, new TimeRange(0, 30))], SegmentSource.Chapter, "old-config");
        var tombstoned = Assert.Single(await database.GetSegmentsAsync(tombstonedItemId));
        await database.DeleteSegmentAsync(tombstonedItemId, tombstoned.Id);

        var deleted = await database.CleanStaleAutomaticSegmentsAsync(itemIds, AnalysisMode.Introduction, "new-config");

        Assert.Equal(1, deleted);
        Assert.Empty(await database.GetSegmentsAsync(staleItemId));
        Assert.Equal(0, await database.CleanStaleAutomaticSegmentsAsync(itemIds, AnalysisMode.Introduction, "new-config"));

        await using var db = _db.Context();
        var remaining = await db.Segments.AsNoTracking().ToListAsync();
        Assert.Equal(4, remaining.Count);
        Assert.Single(remaining, s => s.ItemId == userProvidedItemId && s.Type == AnalysisMode.Introduction && s.Source == SegmentSource.User);
        Assert.Single(remaining, s => s.ItemId == matchingHashItemId && s.Type == AnalysisMode.Introduction && s.ConfigHash == "new-config");
        Assert.Single(remaining, s => s.ItemId == otherModeItemId && s.Type == AnalysisMode.Credits);
        Assert.Single(remaining, s => s.ItemId == tombstonedItemId && s.State == SegmentState.Suppressed);
    }

    [Fact]
    public async Task RebuildDatabaseAsync_PreservesValidSegmentsSeasonStatesAnalysisRecordsDisabledItemsAndImportMarker()
    {
        var automaticItemId = Guid.NewGuid();
        var userProvidedItemId = Guid.NewGuid();
        var tombstonedItemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(automaticItemId, AnalysisMode.Introduction, [new Segment(automaticItemId, new TimeRange(5, 30))], SegmentSource.Chromaprint, "cfg-auto");
        await database.ReplaceAutoSegmentsAsync(automaticItemId, AnalysisMode.Commercial, [new Segment(automaticItemId, new TimeRange(100, 110))], SegmentSource.Chapter);
        await database.SeedUserSegmentAsync(userProvidedItemId, AnalysisMode.Credits, Ticks(1200), Ticks(1260));
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, episodeIds, "cfg-season");
        await database.SetAnalyzerActionAsync(
            seasonId,
            new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Credits] = AnalyzerAction.None });
        await database.SetItemDisabledAsync(seasonId, episodeIds[0], disabled: true);

        // Tombstones record user intent and must survive corruption recovery.
        await database.ReplaceAutoSegmentsAsync(tombstonedItemId, AnalysisMode.Introduction, [new Segment(tombstonedItemId, new TimeRange(0, 30))], SegmentSource.Chapter);
        var tombstoned = Assert.Single(await database.GetSegmentsAsync(tombstonedItemId));
        await database.DeleteSegmentAsync(tombstonedItemId, tombstoned.Id);

        await database.RebuildDatabaseAsync();

        await using var db = _db.Context();
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
        Assert.Equal([episodeIds[0]], await database.GetDisabledItemIdsAsync(seasonId));

        // The import marker survives the rebuild, so the next initialization never
        // re-runs the legacy import on top of the restored rows.
        Assert.True(await db.ImportHistory.AnyAsync());
    }

    [Fact]
    public async Task RebuildDatabaseAsync_DuplicateRanges_KeepUserRowsAndTombstonesOverAutomaticRows()
    {
        var tombstonedItemId = Guid.NewGuid();
        var userItemId = Guid.NewGuid();
        var database = _db.Database;
        await database.InitializeAsync();

        // Repair-era or corrupted files can hold exact-range duplicates the unique
        // index would normally reject; plant them with the index dropped, automatic
        // rows first so a first-wins dedupe would keep the wrong row.
        await using (var db = _db.Context())
        {
            await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_Segments_ItemId_Type_StartTicks_EndTicks\"");
            foreach (var (itemId, source, state) in new[]
            {
                (tombstonedItemId, SegmentSource.Chapter, SegmentState.Active),
                (tombstonedItemId, SegmentSource.Chapter, SegmentState.Suppressed),
                (userItemId, SegmentSource.Chromaprint, SegmentState.Active),
                (userItemId, SegmentSource.User, SegmentState.Active),
            })
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO "Segments" ("Id", "ItemId", "Type", "StartTicks", "EndTicks", "Source", "State", "ConfigHash", "CreatedAt", "UpdatedAt")
                    VALUES ({Guid.NewGuid()}, {itemId}, {(int)AnalysisMode.Introduction}, {Ticks(10)}, {Ticks(20)}, {(int)source}, {(int)state}, '', {DateTime.UtcNow}, {DateTime.UtcNow})
                    """);
            }
        }

        await database.RebuildDatabaseAsync();

        await using var rebuilt = _db.Context();
        var segments = await rebuilt.Segments.AsNoTracking().ToListAsync();
        Assert.Equal(2, segments.Count);

        var tombstone = Assert.Single(segments, s => s.ItemId == tombstonedItemId);
        Assert.Equal(SegmentState.Suppressed, tombstone.State);

        var userRow = Assert.Single(segments, s => s.ItemId == userItemId);
        Assert.Equal(SegmentSource.User, userRow.Source);
    }

    [Fact]
    public async Task RebuildDatabaseAsync_BackupFailure_AbortsAndPreservesFile_WithoutForceClean()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(5, 30))], SegmentSource.Chapter);

        // Deterministic backup failure: dropping a column the model SELECT expects
        // makes the backup read throw SqliteException ("no such column").
        await DropSegmentsConfigHashColumnAsync(_db.Path);

        var exception = await Assert.ThrowsAsync<DatabaseRebuildBackupException>(() => database.RebuildDatabaseAsync());
        Assert.IsType<SqliteException>(exception.InnerException);

        // The aborted rebuild must not have touched the database file: the seeded
        // row is still there. (Raw connection — the EF model no longer matches.)
        await using var connection = new SqliteConnection($"Data Source={_db.Path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"Segments\"";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RebuildDatabaseAsync_BackupFailure_RebuildsClean_WhenForceCleanRequested()
    {
        var itemId = Guid.NewGuid();
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(5, 30))], SegmentSource.Chapter);

        await DropSegmentsConfigHashColumnAsync(_db.Path);

        await database.RebuildDatabaseAsync(forceCleanOnBackupFailure: true);

        // Explicitly requested clean rebuild: the unreadable data is gone and the
        // schema is recreated at the current migration level. No import marker is
        // synthesized: with the rebuilt file empty, the legacy database may be the
        // only copy of the user's data, so the next start must import it.
        await using (var db = _db.Context())
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

    [Fact]
    public async Task RebuildDatabaseAsync_UninitializableDatabase_IsReachableAndRebuildsClean()
    {
        var itemId = Guid.NewGuid();
        // A garbage file fails MigrateAsync itself, so every initialization attempt
        // throws — the state the rebuild endpoint exists to recover from.
        await File.WriteAllTextAsync(_db.Path, "this is not a sqlite database file");
        var database = _db.Database;
        await Assert.ThrowsAsync<SqliteException>(() => database.GetSegmentsAsync(itemId));

        // The rebuild runs despite the failing gate. Without force the unreadable
        // backup aborts it with the data-loss guidance instead of rethrowing the
        // initialization failure.
        var exception = await Assert.ThrowsAsync<DatabaseRebuildBackupException>(() => database.RebuildDatabaseAsync());
        Assert.IsType<SqliteException>(exception.InnerException);

        await database.RebuildDatabaseAsync(forceCleanOnBackupFailure: true);

        // The facade recovers fully: the reset gate migrates the recreated file on
        // the next operation.
        await database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(5, 30))], SegmentSource.Chapter);
        Assert.Single(await database.GetSegmentsAsync(itemId));
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
                return CreateSegmentContext(dbPath);
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
    public async Task CacheOperations_InitializationFailure_ReturnNeutralResults()
    {
        var database = new DetectionCacheDatabase(
            new TestDbContextFactory<DetectionCacheDbContext>(() => throw new IOException("Simulated unavailable cache database.")),
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

        Assert.Equal(0, database.DeleteForItem(itemId));
        Assert.Equal(0, await database.DeleteByModeAsync(AnalysisMode.Introduction));
        Assert.Empty(await database.GetStaleItemIdsAsync(new HashSet<Guid>()));
        Assert.Equal(0, await database.DeleteForItemsAsync([itemId]));
    }

    [Fact]
    public async Task RetryableInitializationGate_FailedAttemptIsSharedAndNextAttemptRetries()
    {
        var attempts = 0;
        var gate = new RetryableInitializationGate(() =>
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
        var episodeId = Guid.NewGuid();

        var facadeA = _db.CreateDatabase();
        var facadeB = _db.CreateDatabase();

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

        await using var db = _db.Context();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // Both gates may have answered the (no-legacy-file) import question before
        // either marker was visible; one or two markers are both fine — the point is
        // that neither initialization failed.
        Assert.True(await db.ImportHistory.CountAsync() >= 1);
    }

    [Fact]
    public async Task Initialization_EnforcesWalJournalMode_OnSegmentDatabase()
    {
        // Simulate a database created or rewritten by external tooling: a valid
        // SQLite file in the default rollback-journal mode. EF only switches to WAL
        // when *it* creates the database file, so initialization must enforce it.
        await using (var connection = new SqliteConnection($"Data Source={_db.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"ExternalToolMarker\" (\"Id\" INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal("delete", GetJournalMode(_db.Path));

        var database = _db.Database;
        await database.InitializeAsync();

        Assert.Equal("wal", GetJournalMode(_db.Path));
    }

    [Fact]
    public void Initialization_EnforcesWalJournalMode_OnCacheDatabase()
    {
        // A pre-existing empty database file: EnsureCreated sees an existing
        // database, creates only the tables, and never applies EF's create-time
        // WAL default — initialization must enforce it.
        using (var connection = new SqliteConnection($"Data Source={_db.Path}"))
        {
            connection.Open();
        }

        Assert.Equal("delete", GetJournalMode(_db.Path));

        var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(_db.Path);
        Assert.True(cacheDatabase.TryInitialize());

        Assert.Equal("wal", GetJournalMode(_db.Path));
    }

    [Fact]
    public async Task CacheDeletes_DatabaseErrorAfterInitialization_IsSwallowedAndReturnsZero()
    {
        // First context (initialization) uses a working path; every later context points
        // into a nonexistent directory, so the delete statements themselves fail with a
        // SqliteException. The facade's deletes are best-effort and must swallow it.
        var goodPath = _db.Path;
        var badPath = Path.Join(
            Path.GetTempPath(),
            "IntroSkipper.Tests",
            Guid.NewGuid().ToString("N") + "-missing-dir",
            "cache.db");
        var contextCreations = 0;
        var database = new DetectionCacheDatabase(
            new TestDbContextFactory<DetectionCacheDbContext>(() =>
                CreateCacheContext(Interlocked.Increment(ref contextCreations) == 1 ? goodPath : badPath)),
            NullLogger<DetectionCacheDatabase>.Instance);

        Assert.True(database.TryInitialize());

        Assert.Equal(0, database.DeleteForItem(Guid.NewGuid()));
        Assert.Equal(0, await database.DeleteByModeAsync(AnalysisMode.Introduction));
        Assert.Equal(0, await database.DeleteForItemsAsync([Guid.NewGuid()]));
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

    [Fact]
    public async Task UpdateSegment_ExactRangeClaimedBetweenReadAndSave_MergesIntoTheClaimant()
    {
        var itemId = Guid.NewGuid();
        var claimant = new DbSegment(itemId, AnalysisMode.Introduction, Ticks(200), Ticks(260), SegmentSource.Chromaprint, "hash");
        var (database, armHook) = await CreateHookedSegmentDatabaseAsync(_db.Path);
        var row = await database.SeedUserSegmentAsync(itemId, AnalysisMode.Introduction, Ticks(10), Ticks(60));

        // Analyzers do not take the editor's stripe: an analysis insert can claim
        // the exact target range between the occupant read and the save. The update
        // must resolve it like an up-front occupant — the claimant survives as the
        // user segment and the moved row is absorbed — instead of surfacing the
        // unique-index violation as a 500. The claimant lands on the update's own
        // connection, ahead of the save's savepoint, so it is visible to the
        // recovery read the way a committed concurrent insert would be.
        armHook(db => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Segments" ("Id", "ItemId", "Type", "StartTicks", "EndTicks", "Source", "State", "ConfigHash", "CreatedAt", "UpdatedAt")
            VALUES ({claimant.Id}, {itemId}, {(int)claimant.Type}, {claimant.StartTicks}, {claimant.EndTicks}, {(int)claimant.Source}, {(int)claimant.State}, {claimant.ConfigHash}, {DateTime.UtcNow}, {DateTime.UtcNow})
            """));

        var updated = await database.UpdateSegmentAsync(itemId, row.Id, Ticks(200), Ticks(260));

        Assert.NotNull(updated);
        Assert.Equal(claimant.Id, updated!.Id);
        Assert.Equal(SegmentSource.User, updated.Source);
        var survivor = Assert.Single(await database.GetSegmentsAsync(itemId, includeSuppressed: true));
        Assert.Equal(claimant.Id, survivor.Id);
        Assert.Equal(Ticks(200), survivor.StartTicks);
        Assert.Equal(Ticks(260), survivor.EndTicks);
        Assert.Equal(SegmentSource.User, survivor.Source);
    }

    /// <summary>
    /// Creates a facade whose contexts run a one-shot callback right before their next
    /// SaveChanges, simulating a concurrent (non-striped) writer landing in the
    /// read-to-save window of a facade operation.
    /// </summary>
    private static async Task<(IntroSkipperDatabase Database, Action<Func<IntroSkipperDbContext, Task>> ArmHook)> CreateHookedSegmentDatabaseAsync(string dbPath)
    {
        // Migrations are discovered per context type, so the subclass context cannot
        // apply them; initialize the schema through a plain facade first.
        await DatabaseTestHelpers.CreateSegmentDatabase(dbPath).InitializeAsync();

        Func<IntroSkipperDbContext, Task>? hook = null;
        var database = new IntroSkipperDatabase(
            new TestDbContextFactory<IntroSkipperDbContext>(
                () => new BeforeSaveHookContext(CreateContextOptions<IntroSkipperDbContext>(dbPath), () => Interlocked.Exchange(ref hook, null))),
            NullLogger<IntroSkipperDatabase>.Instance);
        return (database, callback => hook = callback);
    }

    private sealed class BeforeSaveHookContext(DbContextOptions<IntroSkipperDbContext> options, Func<Func<IntroSkipperDbContext, Task>?> takeHook) : IntroSkipperDbContext(options)
    {
        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            if (takeHook() is { } hook)
            {
                await hook(this).ConfigureAwait(false);
            }

            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }
    }
}
