// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for the SnapPoints endpoint's cache normalization: silence and keyframes are
/// cached absolute and pass through; black intervals are relative to the credits scan
/// start and are only exposed when that anchor is recoverable and the values stay inside
/// the row's scanned window. Also covers the detection-cache per-item query it rests on.
/// </summary>
public sealed class TestSnapPoints
{
    [Fact]
    public async Task SnapPoints_ReturnsAbsoluteKeyframesAndSilence_SortedAndDeduped()
    {
        using var scope = CreateScope(out var itemId, out var cacheDb, out var controller);
        cacheDb.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Keyframe, 0, 300, DetectionCacheService.CompressBrotli<double[]>([20, 10, 20.0005]), "h");
        cacheDb.Upsert(itemId, AnalysisMode.Credits, CacheEntryType.Keyframe, 1000, 1200, DetectionCacheService.CompressBrotli<double[]>([1100]), "h");
        cacheDb.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, 0, 300, DetectionCacheService.CompressBrotli<TimeRange[]>([new TimeRange(50, 55)]), "h");

        var result = await GetSnapPointsAsync(controller, itemId);

        Assert.True(result.FromCache);
        Assert.Equal([10, 20, 1100], result.Keyframes);
        var silence = Assert.Single(result.Silence);
        Assert.Equal(50, silence.Start);
        Assert.Equal(55, silence.End);
        Assert.Empty(result.BlackIntervals);
    }

    [Fact]
    public async Task SnapPoints_NormalizesBlackIntervals_WithBlackFrameAnchor()
    {
        using var scope = CreateScope(out var itemId, out var cacheDb, out var controller);

        // Whole-scan black-frame row: Start carries the write-time credits scan start.
        cacheDb.Upsert(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, 900, 0, DetectionCacheService.CompressBrotli<BlackInterval[]>([]), "h");
        cacheDb.Upsert(
            itemId,
            AnalysisMode.Credits,
            CacheEntryType.BlackInterval,
            905,
            915,
            DetectionCacheService.CompressBrotli<BlackInterval[]>([new BlackInterval(6.5, 7.25)]),
            "h");

        var result = await GetSnapPointsAsync(controller, itemId);

        Assert.True(result.FromCache);
        var interval = Assert.Single(result.BlackIntervals);
        Assert.Equal(906.5, interval.Start);
        Assert.Equal(907.25, interval.End);
    }

    [Fact]
    public async Task SnapPoints_UsesQueuedEpisodeAnchor_WhenNoAnchorRowExists()
    {
        using var scope = CreateScope(out var itemId, out var cacheDb, out var controller);
        Plugin.Instance!.QueuedMediaItems[Guid.NewGuid()] =
        [
            new QueuedEpisode { EpisodeId = itemId, CreditsFingerprintStart = 1000 },
        ];
        cacheDb.Upsert(
            itemId,
            AnalysisMode.Credits,
            CacheEntryType.BlackInterval,
            1005,
            1015,
            DetectionCacheService.CompressBrotli<BlackInterval[]>([new BlackInterval(6, 7)]),
            "h");

        var result = await GetSnapPointsAsync(controller, itemId);

        var interval = Assert.Single(result.BlackIntervals);
        Assert.Equal(1006, interval.Start);
        Assert.Equal(1007, interval.End);
    }

    [Fact]
    public async Task SnapPoints_OmitsBlackIntervals_WithoutAnchor_AndDropsOutOfWindowValues()
    {
        using var scope = CreateScope(out var itemId, out var cacheDb, out var controller);
        cacheDb.Upsert(
            itemId,
            AnalysisMode.Credits,
            CacheEntryType.BlackInterval,
            905,
            915,
            DetectionCacheService.CompressBrotli<BlackInterval[]>([new BlackInterval(6, 7)]),
            "h");

        // No anchor row and no queue entry: intervals must be omitted, not guessed.
        var withoutAnchor = await GetSnapPointsAsync(controller, itemId);
        Assert.True(withoutAnchor.FromCache);
        Assert.Empty(withoutAnchor.BlackIntervals);

        // With an anchor whose absolute values fall outside the row's scanned window,
        // the values are dropped as stale.
        cacheDb.Upsert(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, 500, 0, DetectionCacheService.CompressBrotli<BlackInterval[]>([]), "h");
        var withStaleAnchor = await GetSnapPointsAsync(controller, itemId);
        Assert.Empty(withStaleAnchor.BlackIntervals);
    }

    [Fact]
    public async Task SnapPoints_TwoAnalysisEras_ResolvesEachRowAgainstItsOwnAnchor()
    {
        using var scope = CreateScope(out var itemId, out var cacheDb, out var controller);

        // Production hashes embed the entry type, so a BlackInterval row's hash never
        // equals a BlackFrame row's. Era identity therefore only exists for rows the
        // CURRENT configuration wrote; historical rows are resolved by geometry alone.
        var config = Plugin.Instance!.Configuration;
        var currentBlackFrameHash = ConfigHasher.DetectionCache(config, CacheEntryType.BlackFrame, AnalysisMode.Credits);
        var currentIntervalHash = ConfigHasher.DetectionCache(config, CacheEntryType.BlackInterval, AnalysisMode.Credits);

        // Era 1 (historical config) scanned credits from 1200s; its probe window
        // [1250,1350] holds an interval at absolute 1300-1304, stored relative as (100,104).
        // Only the era-1 anchor fits geometrically, so it resolves without a tiebreak.
        cacheDb.Upsert(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, 1200, 0, DetectionCacheService.CompressBrotli<BlackFrame[]>([]), "era1");
        cacheDb.Upsert(
            itemId,
            AnalysisMode.Credits,
            CacheEntryType.BlackInterval,
            1250,
            1350,
            DetectionCacheService.CompressBrotli<BlackInterval[]>([new BlackInterval(100, 104)]),
            "era1");

        // Era 2 (current config) scanned from 1020s; window [1020,1400] holds an interval
        // at absolute 1100-1104, stored relative as (80,84). Both anchors fit that wide
        // window, so only the current-era tiebreak can place it.
        cacheDb.Upsert(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, 1020, 0, DetectionCacheService.CompressBrotli<BlackFrame[]>([]), currentBlackFrameHash);
        cacheDb.Upsert(
            itemId,
            AnalysisMode.Credits,
            CacheEntryType.BlackInterval,
            1020,
            1400,
            DetectionCacheService.CompressBrotli<BlackInterval[]>([new BlackInterval(80, 84)]),
            currentIntervalHash);

        // A historical row whose wide window fits both anchors stays ambiguous — no era
        // identity can be established for it, so it must be omitted, not guessed.
        cacheDb.Upsert(
            itemId,
            AnalysisMode.Credits,
            CacheEntryType.BlackInterval,
            1000,
            1400,
            DetectionCacheService.CompressBrotli<BlackInterval[]>([new BlackInterval(150, 154)]),
            "era1");

        var result = await GetSnapPointsAsync(controller, itemId);

        Assert.Equal(2, result.BlackIntervals.Count);
        Assert.Contains(result.BlackIntervals, interval => Math.Abs(interval.Start - 1100) < 0.01 && Math.Abs(interval.End - 1104) < 0.01);
        Assert.Contains(result.BlackIntervals, interval => Math.Abs(interval.Start - 1300) < 0.01 && Math.Abs(interval.End - 1304) < 0.01);
    }

    [Fact]
    public async Task SnapPoints_CorruptPayload_IsSkipped_NotFatal()
    {
        using var scope = CreateScope(out var itemId, out var cacheDb, out var controller);

        // Not valid Brotli/JSON: the best-effort endpoint must skip the row, not fail.
        cacheDb.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Keyframe, 0, 300, [1, 2, 3], "h");
        cacheDb.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, 0, 300, DetectionCacheService.CompressBrotli<TimeRange[]>([new TimeRange(50, 55)]), "h");
        cacheDb.Upsert(itemId, AnalysisMode.Credits, CacheEntryType.BlackInterval, 905, 915, [7, 7, 7], "h");

        var result = await GetSnapPointsAsync(controller, itemId);

        Assert.True(result.FromCache);
        Assert.Empty(result.Keyframes);
        Assert.Empty(result.BlackIntervals);
        var silence = Assert.Single(result.Silence);
        Assert.Equal(50, silence.Start);
        Assert.Equal(55, silence.End);
    }

    [Fact]
    public async Task SnapPoints_EmptyCache_ReturnsEmptyArraysNotFromCache()
    {
        using var scope = CreateScope(out var itemId, out _, out var controller);

        var result = await GetSnapPointsAsync(controller, itemId);

        Assert.False(result.FromCache);
        Assert.Empty(result.Keyframes);
        Assert.Empty(result.Silence);
        Assert.Empty(result.BlackIntervals);
    }

    [Fact]
    public async Task SnapPoints_Returns404_ForUnknownItem()
    {
        using var scope = CreateScope(out _, out _, out var controller);

        var response = await controller.GetSnapPointsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task GetEntriesForItemAsync_FiltersByItemAndType_AndReturnsRawPayloads()
    {
        var cacheDb = DatabaseTestHelpers.CreateCacheDatabase(DatabaseTestHelpers.CreateTempCacheDbPath());
        var itemId = Guid.NewGuid();
        var payload = DetectionCacheService.CompressBrotli<double[]>([1, 2, 3]);
        cacheDb.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Keyframe, 0, 300, payload, "hash-a");
        cacheDb.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, 0, 300, DetectionCacheService.CompressBrotli<TimeRange[]>([]), "hash-b");
        cacheDb.Upsert(Guid.NewGuid(), AnalysisMode.Introduction, CacheEntryType.Keyframe, 0, 300, payload, "hash-c");

        var entries = await cacheDb.GetEntriesForItemAsync(itemId, [CacheEntryType.Keyframe]);

        var entry = Assert.Single(entries);
        Assert.Equal(itemId, entry.ItemId);
        Assert.Equal(CacheEntryType.Keyframe, entry.Type);
        Assert.Equal("hash-a", entry.ConfigHash);
        var decompressed = DetectionCacheService.DecompressBrotli<double[]>(entry.Data);
        Assert.NotNull(decompressed);
        Assert.Equal([1, 2, 3], decompressed);

        Assert.Empty(await cacheDb.GetEntriesForItemAsync(itemId, []));
    }

    [Fact]
    public async Task CacheReads_DegradeToEmpty_WhenTheTableIsUnreadable()
    {
        var dbPath = DatabaseTestHelpers.CreateTempCacheDbPath();
        var cacheDb = DatabaseTestHelpers.CreateCacheDatabase(dbPath);
        var itemId = Guid.NewGuid();
        cacheDb.Upsert(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, 900, 0, DetectionCacheService.CompressBrotli<BlackInterval[]>([]), "h");

        // Dropping the table makes every read throw SqliteException. The drop lands after
        // the initialization gate has already cached its success, so the schema cannot be
        // recreated underneath the read.
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE \"DetectionCache\"";
            await command.ExecuteNonQueryAsync();
        }

        // The cache is an optimization, so both readers report absence rather than
        // failing the best-effort endpoint that rests on them.
        Assert.Empty(await cacheDb.GetEntriesForItemAsync(itemId, [CacheEntryType.BlackFrame]));
        Assert.Empty(await cacheDb.GetEntryRangesForItemAsync(itemId, [CacheEntryType.BlackFrame]));
    }

    private static EntrypointTestHelpers.PluginInstanceScope CreateScope(
        out Guid itemId,
        out IDetectionCacheDatabase cacheDb,
        out SegmentEditorController controller)
    {
        var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        itemId = Guid.NewGuid();
        var movie = EntrypointTestHelpers.CreateMovie(itemId);
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager(movie));
        EntrypointTestHelpers.SetPropertyOrField(
            Plugin.Instance!,
            "QueuedMediaItems",
            new ConcurrentDictionary<Guid, List<QueuedEpisode>>());

        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var store = new FakeJellyfinSegmentStore();
        cacheDb = DatabaseTestHelpers.CreateCacheDatabase(DatabaseTestHelpers.CreateTempCacheDbPath());
        controller = new SegmentEditorController(
            new MediaSegmentEditorService(store, database, [], NullLogger<MediaSegmentEditorService>.Instance),
            database,
            cacheDb);
        return scope;
    }

    private static async Task<SnapPointsResponse> GetSnapPointsAsync(SegmentEditorController controller, Guid itemId)
    {
        var response = await controller.GetSnapPointsAsync(itemId, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<SnapPointsResponse>(ok.Value);
    }
}
