// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestCacheOperations
{
    private const string MostChannelsStreamCacheVariant = "policy=most-channels";

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    public async Task DeleteByMode_DeletesOnlyThatModesEntries(AnalysisMode mode)
    {
        var itemId = Guid.NewGuid();
        using var scope = new CachingPluginScope();
        var entries = new (AnalysisMode Mode, CacheEntryType Type, double Start, double End)[]
        {
            (AnalysisMode.Credits, CacheEntryType.Chromaprint, 0, 0),
            (AnalysisMode.Credits, CacheEntryType.BlackFrame, 100.5, 0),
            (AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0),
            (AnalysisMode.Introduction, CacheEntryType.Silence, 0, 30),
            (AnalysisMode.Introduction, CacheEntryType.Keyframe, 0, 30),
            (AnalysisMode.Introduction, CacheEntryType.BlackFrame, 0, 30),
        };
        foreach (var (entryMode, type, start, end) in entries)
        {
            scope.SeedRow(itemId, entryMode, type, EntrypointTestHelpers.EmptyJsonArray, start, end);
        }

        var deleted = await scope.CacheDatabase.DeleteByModeAsync(mode);

        Assert.Equal(entries.Count(e => e.Mode == mode), deleted);
        using var db = DatabaseTestHelpers.CreateCacheContext(scope.CacheDbPath);
        Assert.False(db.DetectionCache.Any(e => e.ItemId == itemId && e.Mode == mode));
        Assert.Equal(entries.Count(e => e.Mode != mode), db.DetectionCache.Count(e => e.ItemId == itemId));
    }

    /// <summary>
    /// Regression test: a cached empty array ("[]") must be treated as a valid cache hit.
    /// Before the fix, cache reads returned false for empty arrays, causing unnecessary re-analysis.
    /// </summary>
    [Fact]
    public async Task EmptyArrayCacheEntry_TreatedAsCacheHit()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
        };
        var range = new TimeRange(0, 30);
        using var scope = new CachingPluginScope();

        // The cache row must be Brotli-compressed because DetectionCacheService.TryRead decompresses DB payloads.
        scope.SeedRow(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Silence, DetectionCacheService.CompressBrotli(Array.Empty<TimeRange>()), range.Start, range.End);

        // If the empty-array bug were present this would throw FingerprintException (file not found).
        var result = await scope.CreateFFmpegService().DetectSilenceAsync(episode, range, AnalysisMode.Introduction);

        Assert.Empty(result);
    }

    [Fact]
    public void StreamScopedChromaprintCache_AcceptsMatchingLegacyDefaultHash()
    {
        var episodeId = Guid.NewGuid();
        uint[] fingerprint = [111u, 222u];
        using var scope = new CachingPluginScope();
        var legacyHash = ConfigHasher.LegacyChromaprintCacheWithoutLanguage(Plugin.Instance!.Configuration, AnalysisMode.Introduction);

        scope.SeedRow(episodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, DetectionCacheService.CompressBrotli(fingerprint), 0, 600, legacyHash);

        Assert.True(scope.CacheService.TryRead(
            episodeId,
            AnalysisMode.Introduction,
            CacheEntryType.Chromaprint,
            0,
            600,
            out uint[] result,
            MostChannelsStreamCacheVariant,
            legacyHash));
        Assert.Equal(fingerprint, result);
    }

    [Fact]
    public async Task CachedBlackIntervals_UsesCreditsFingerprintRange()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            CreditsFingerprintStart = 1560,
            CreditsFingerprintEnd = 1800,
            Duration = 2400,
        };
        var intervals = new BlackInterval[] { new(10, 20), new(30.5, 35) };
        using var scope = new CachingPluginScope();
        scope.SeedRow(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackInterval, DetectionCacheService.CompressBrotli(intervals), 1560, 1800);

        var result = await scope.CreateFFmpegService().DetectBlackIntervalsAsync(episode, new TimeRange(1560, 1800), 32, 85);

        Assert.Equal(intervals, result);
    }

    [Fact]
    public async Task CachedFingerprint_StoresRealStartEnd()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 600,
        };
        var fingerprint = new uint[] { 111u, 222u, 333u };
        using var scope = new CachingPluginScope();

        // A row at the episode's real range (start 0, end = IntroFingerprintEnd) is the hit.
        scope.SeedRow(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, DetectionCacheService.CompressBrotli(fingerprint), 0, 600);

        var result = await scope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Introduction);

        Assert.Equal(fingerprint, result);
    }

    [Fact]
    public async Task CachedFingerprint_ThrowsWhenCanceledBeforeCacheHit()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 600,
        };
        using var scope = new CachingPluginScope();
        scope.SeedRow(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, DetectionCacheService.CompressBrotli(new uint[] { 111u, 222u, 333u }), 0, 600);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => scope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Introduction, cts.Token));
    }

    // The episode expects an intro fingerprint over 0-600 s and a credits fingerprint
    // over 1560-1800 s; a row counts only when its range matches and its hash is one a
    // read path accepts (the pre-stream-selection legacy hash included, so already
    // analyzed episodes rejoin the Chromaprint comparison pool after an upgrade).
    [Theory]
    [InlineData(AnalysisMode.Introduction, false, 0, 0, false, false)]
    [InlineData(AnalysisMode.Introduction, true, 0, 900, false, false)]
    [InlineData(AnalysisMode.Introduction, true, 0, 600, false, true)]
    [InlineData(AnalysisMode.Introduction, true, 0, 600, true, true)]
    [InlineData(AnalysisMode.Credits, true, 1560, 1800, false, true)]
    public void HasCachedFingerprint_MatchesRowsByRangeAndAcceptedHash(AnalysisMode mode, bool seedRow, double rowStart, double rowEnd, bool legacyHash, bool expected)
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            IntroFingerprintEnd = 600,
            CreditsFingerprintStart = 1560,
            Duration = 1800,
        };
        using var scope = new CachingPluginScope();
        if (seedRow)
        {
            scope.SeedRow(
                episode.EpisodeId,
                mode,
                CacheEntryType.Chromaprint,
                EntrypointTestHelpers.EmptyJsonArray,
                rowStart,
                rowEnd,
                legacyHash ? ConfigHasher.LegacyChromaprintCacheWithoutLanguage(new PluginConfiguration(), mode) : string.Empty);
        }

        Assert.Equal(expected, scope.CacheService.HasCachedFingerprint(episode, mode));
    }

    [Fact]
    public async Task DeleteUnreadableEntriesAsync_DeletesOnlyRowsNoReadPathAccepts()
    {
        var itemId = Guid.NewGuid();
        using var scope = new CachingPluginScope();
        var config = Plugin.Instance!.Configuration;
        var cacheDatabase = scope.CacheDatabase;

        // One row per acceptance path, distinguished by their range keys, plus one row whose
        // hash no read path accepts any more (e.g. written by the intermediate release that
        // suffixed the legacy Chromaprint hash input with empty audio tokens).
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 100, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.DetectionCache(config, CacheEntryType.Chromaprint, AnalysisMode.Introduction));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 200, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.LegacyChromaprintCacheWithoutLanguage(config, AnalysisMode.Introduction));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 300, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.DetectionCache(config, CacheEntryType.Chromaprint, AnalysisMode.Introduction, MostChannelsStreamCacheVariant));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 400, EntrypointTestHelpers.EmptyJsonArray, string.Empty);
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, 0, 500, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.DetectionCache(config, CacheEntryType.Silence, AnalysisMode.Introduction));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 600, EntrypointTestHelpers.EmptyJsonArray, "0123456789ABCDEF");

        var deleted = await scope.CacheService.DeleteUnreadableEntriesAsync();

        Assert.Equal(1, deleted);
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 100));
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 200));
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 300));
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 400));
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, 0, 500));
        Assert.Null(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 600));
    }

    /// <summary>
    /// A plugin instance with fingerprint caching enabled over a fresh cache database,
    /// plus the cache facade and service the ffmpeg service reads through.
    /// </summary>
    private sealed class CachingPluginScope : IDisposable
    {
        private readonly EntrypointTestHelpers.PluginInstanceScope _inner;

        public CachingPluginScope()
        {
            _inner = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { CacheFingerprints = true });
            CacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(_inner.CacheDbPath);
            CacheService = new DetectionCacheService(NullLogger<DetectionCacheService>.Instance, CacheDatabase);
        }

        public DetectionCacheDatabase CacheDatabase { get; }

        public DetectionCacheService CacheService { get; }

        public string CacheDbPath => _inner.CacheDbPath;

        public FFmpegService CreateFFmpegService() => new(NullLogger<FFmpegService>.Instance, CacheService);

        /// <summary>
        /// Stores one raw cache row. Pass the bytes a read path expects (Brotli for
        /// payloads the service decompresses).
        /// </summary>
        public void SeedRow(Guid itemId, AnalysisMode mode, CacheEntryType type, byte[] data, double start = 0, double end = 0, string configHash = "")
            => CacheDatabase.Upsert(itemId, mode, type, start, end, data, configHash);

        public void Dispose() => _inner.Dispose();
    }
}
