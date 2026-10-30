// SPDX-FileCopyrightText: 2026 rlauuzo
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
using IntroSkipper.Manager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for <see cref="ItemRebinder"/> and the rebind it commits through
/// <see cref="IntroSkipperDatabase.RebindItemsAsync"/>: a renamed file keeps its rows.
/// </summary>
public sealed class TestItemRebinder : IDisposable
{
    private readonly TempSegmentDb _db = new();
    private readonly TempDirectory _dir = new();
    private readonly HashSet<Guid> _gone = [];
    private readonly Guid _oldSeason = Guid.NewGuid();
    private readonly Guid _newSeason = Guid.NewGuid();
    private readonly Guid _orphan = Guid.NewGuid();
    private readonly Guid _item = Guid.NewGuid();

    [Fact]
    public async Task RenamedFile_TakesEveryRowOfItsOrphan()
    {
        var database = _db.Database;
        var path = WriteFile("episode.mkv", 1);
        await RebindAsync(Season(_oldSeason, (_orphan, path, 100)));
        var sibling = Guid.NewGuid();
        await SeedOrphanAsync(database);
        await database.SetItemDisabledAsync(_orphan, disabled: true);
        await database.RecordSettleReanalysisAsync(_oldSeason, [AnalysisMode.Introduction], [_orphan, sibling]);
        await database.SetAnalysisOverridesAsync(_oldSeason, 40, null, null, null, null);

        // A Recap record written with subtitle detection carries a sidecar version, not the media's.
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Recap, [(_orphan, 555L)], "h");
        _gone.UnionWith([_orphan, _oldSeason]);

        // The season folder was renamed too, and the copy did not keep the write time.
        await RebindAsync(Season(_newSeason, (_item, path, 200)));

        Assert.Empty(await database.GetSegmentsAsync(_orphan, includeSuppressed: true));
        var segments = await database.GetSegmentsAsync(_item, includeSuppressed: true);
        Assert.Equal(3, segments.Count);
        Assert.Contains(segments, s => s.Source == SegmentSource.User);
        Assert.Contains(segments, s => s.State == SegmentState.Suppressed);
        Assert.Equal([_item], await database.GetDisabledItemIdsAsync([_item, _orphan]));
        Assert.Equal(40, (await database.GetAnalysisOverridesAsync(_newSeason)).AnalysisPercent);

        await using var db = _db.Context();
        var records = await db.AnalyzedItems.ToDictionaryAsync(a => a.Type);
        Assert.Equal((_item, 200L), (records[AnalysisMode.Introduction].ItemId, records[AnalysisMode.Introduction].FileVersion));
        Assert.Equal((_item, 555L), (records[AnalysisMode.Recap].ItemId, records[AnalysisMode.Recap].FileVersion));
        var state = await db.SeasonStates.SingleAsync();
        Assert.Equal(_newSeason, state.SeasonId);
        Assert.Equal([_item, sibling], state.SettledReanalysisEpisodeIds);
        Assert.Equal(_item, (await db.FileHashes.SingleAsync()).ItemId);
        Assert.Contains(_item, await db.ProjectionQueue.Select(q => q.ItemId).ToListAsync());
    }

    [Fact]
    public async Task AnalyzedItem_TakesTheIntentAndReopensTheModesItCovers()
    {
        var database = _db.Database;
        var path = WriteFile("episode.mkv", 1);
        await RebindAsync(Season(_oldSeason, (_orphan, path, 100)));
        await SeedOrphanAsync(database);

        // Copied before the original was deleted: the new item was analyzed on its own.
        await database.ReplaceAutoSegmentsAsync(_item, AnalysisMode.Introduction, [new Segment(_item, new TimeRange(12, 42))], SegmentSource.Chromaprint, "h");
        await database.ReplaceAutoSegmentsAsync(_item, AnalysisMode.Credits, [new Segment(_item, new TimeRange(1300, 1400))], SegmentSource.Combined, "h");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(_item, 200L)], "h");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [(_item, 200L)], "h");
        _gone.Add(_orphan);

        await RebindAsync(Season(_newSeason, (_item, path, 200)));

        var segments = await database.GetSegmentsAsync(_item, includeSuppressed: true);
        Assert.Equivalent(
            new[] { (AnalysisMode.Introduction, SegmentSource.Chromaprint), (AnalysisMode.Credits, SegmentSource.User), (AnalysisMode.Recap, SegmentSource.Chromaprint) },
            segments.Select(s => (s.Type, s.Source)));
        Assert.Equal([AnalysisMode.Introduction], await AnalyzedModesAsync(_item));

        // The orphan's own analysis stays behind for the cleanup task.
        Assert.Equal(AnalysisMode.Introduction, Assert.Single(await database.GetSegmentsAsync(_orphan)).Type);
    }

    [Fact]
    public async Task ItemWithItsOwnIntent_TakesNothing()
    {
        var database = _db.Database;
        var path = WriteFile("episode.mkv", 1);
        await RebindAsync(Season(_oldSeason, (_orphan, path, 100)));
        await SeedOrphanAsync(database);

        // The copy holds a tombstone at the very range of the orphan's automatic intro, and no analysis.
        await database.ReplaceAutoSegmentsAsync(_item, AnalysisMode.Introduction, [new Segment(_item, new TimeRange(10, 40))], SegmentSource.Chromaprint, "h");
        await database.DeleteSegmentAsync(_item, Assert.Single(await database.GetSegmentsAsync(_item)).Id);
        _gone.Add(_orphan);

        await RebindAsync(Season(_newSeason, (_item, path, 200)));

        Assert.Equal(SegmentState.Suppressed, Assert.Single(await database.GetSegmentsAsync(_item, includeSuppressed: true)).State);
        Assert.Empty(await AnalyzedModesAsync(_item));
        Assert.Equal(3, (await database.GetSegmentsAsync(_orphan, includeSuppressed: true)).Count);

        // The rebind committed rather than failing: the orphan's claim is spent.
        Assert.Empty(await database.GetFileHashesAsync([_orphan]));
    }

    [Theory]
    [InlineData(SeasonCase.OldSeasonStillExists)]
    [InlineData(SeasonCase.NewSeasonHasState)]
    [InlineData(SeasonCase.NewSeasonHasAnotherItem)]
    [InlineData(SeasonCase.OldSeasonSplit)]
    public async Task SeasonState_MovesOnlyWithAWholeGoneSeason(SeasonCase seasonCase)
    {
        var database = _db.Database;
        var path = WriteFile("episode.mkv", 1);
        var otherPath = WriteFile("other.mkv", 2);
        var other = Guid.NewGuid();
        var splitSeason = Guid.NewGuid();
        await RebindAsync(seasonCase == SeasonCase.OldSeasonSplit
            ? Season(_oldSeason, (_orphan, path, 100), (other, otherPath, 100))
            : Season(_oldSeason, (_orphan, path, 100)));
        await database.SetAnalysisOverridesAsync(_oldSeason, 40, null, null, null, null);
        await SeedOrphanAsync(database);
        _gone.UnionWith([_orphan, other]);
        if (seasonCase != SeasonCase.OldSeasonStillExists)
        {
            _gone.Add(_oldSeason);
        }

        if (seasonCase == SeasonCase.NewSeasonHasState)
        {
            await database.SetAnalysisOverridesAsync(_newSeason, 70, null, null, null, null);
        }

        await RebindAsync(seasonCase == SeasonCase.NewSeasonHasAnotherItem
            ? Season(_newSeason, (_item, path, 200), (Guid.NewGuid(), otherPath, 200))
            : Season(_newSeason, (_item, path, 200)));
        if (seasonCase == SeasonCase.OldSeasonSplit)
        {
            // The other half lands in a season of its own: it must not take the old state either.
            await RebindAsync(Season(splitSeason, (Guid.NewGuid(), otherPath, 200)));
        }

        Assert.Equal(3, (await database.GetSegmentsAsync(_item, includeSuppressed: true)).Count);
        Assert.Equal(seasonCase == SeasonCase.OldSeasonStillExists ? 40 : null, (await database.GetAnalysisOverridesAsync(_oldSeason)).AnalysisPercent);
        Assert.Equal(seasonCase == SeasonCase.NewSeasonHasState ? 70 : null, (await database.GetAnalysisOverridesAsync(_newSeason)).AnalysisPercent);
        Assert.Null((await database.GetAnalysisOverridesAsync(splitSeason)).AnalysisPercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoSingleOrphan_RebindsNothing(bool twoOrphans)
    {
        var database = _db.Database;
        var path = WriteFile("episode.mkv", 1);
        await RebindAsync(Season(_oldSeason, (_orphan, path, 100)));
        await SeedOrphanAsync(database);
        if (twoOrphans)
        {
            var secondOrphan = Guid.NewGuid();
            await RebindAsync(Season(Guid.NewGuid(), (secondOrphan, path, 100)));
            _gone.UnionWith([_orphan, secondOrphan]);
        }

        // Otherwise the original is still in the library: the new item is a copy.
        await RebindAsync(Season(_newSeason, (_item, path, 200)));

        Assert.Empty(await database.GetSegmentsAsync(_item, includeSuppressed: true));
        Assert.Equal(3, (await database.GetSegmentsAsync(_orphan, includeSuppressed: true)).Count);
    }

    [Fact]
    public async Task UnchangedFileVersion_KeepsTheStoredHash()
    {
        var path = WriteFile("episode.mkv", 1);
        await RebindAsync(Season(_oldSeason, (_item, path, 100)));
        var first = await StoredHashAsync(_item);

        WriteFile("episode.mkv", 2);
        await RebindAsync(Season(_oldSeason, (_item, path, 100)));
        Assert.Equal(first, await StoredHashAsync(_item));

        await RebindAsync(Season(_oldSeason, (_item, path, 101)));
        Assert.NotEqual(first, await StoredHashAsync(_item));
    }

    /// <summary>
    /// Ways a rebound season differs from the one its orphans left.
    /// </summary>
    public enum SeasonCase
    {
        /// <summary>The orphans' season key still resolves.</summary>
        OldSeasonStillExists,

        /// <summary>The new season key already has overrides of its own.</summary>
        NewSeasonHasState,

        /// <summary>The new season also holds an item that was not rebound (a merge).</summary>
        NewSeasonHasAnotherItem,

        /// <summary>The old season's two items land in two new seasons (a split).</summary>
        OldSeasonSplit,
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private static ResolvedSeason Season(Guid key, params (Guid Id, string Path, long FileVersion)[] episodes)
        => new(key, Guid.NewGuid(), [.. episodes.Select(e => new QueuedEpisode { EpisodeId = e.Id, SeasonId = key, Path = e.Path, Name = "Episode", FileVersion = e.FileVersion })]);

    // An automatic Introduction row with its record, a user Credits row and a Recap tombstone.
    private async Task SeedOrphanAsync(IntroSkipperDatabase database)
    {
        await database.ReplaceAutoSegmentsAsync(_orphan, AnalysisMode.Introduction, [new Segment(_orphan, new TimeRange(10, 40))], SegmentSource.Chromaprint, "h");
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(_orphan, 100L)], "h");
        await database.SeedUserSegmentAsync(_orphan, AnalysisMode.Credits, DatabaseTestHelpers.Ticks(1290), DatabaseTestHelpers.Ticks(1400));
        await database.ReplaceAutoSegmentsAsync(_orphan, AnalysisMode.Recap, [new Segment(_orphan, new TimeRange(0, 30))], SegmentSource.Chromaprint, "h");
        var recap = Assert.Single(await database.GetSegmentsAsync(_orphan), s => s.Type == AnalysisMode.Recap);
        await database.DeleteSegmentAsync(_orphan, recap.Id);
    }

    private Task RebindAsync(ResolvedSeason season)
        => new ItemRebinder(_db.Database, _gone.Contains, NullLogger.Instance).RebindAsync(season, CancellationToken.None);

    private string WriteFile(string name, byte fill)
    {
        var path = _dir.Join(name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, 4 * 64 * 1024).ToArray());
        return path;
    }

    private async Task<List<AnalysisMode>> AnalyzedModesAsync(Guid itemId)
    {
        await using var db = _db.Context();
        return await db.AnalyzedItems.Where(a => a.ItemId == itemId).Select(a => a.Type).ToListAsync();
    }

    private async Task<string> StoredHashAsync(Guid itemId)
        => (await _db.Database.GetFileHashesAsync([itemId]))[itemId].Hash;
}
