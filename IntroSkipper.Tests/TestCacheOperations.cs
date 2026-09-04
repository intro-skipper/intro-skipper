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
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var entries = new DbDetectionCache[]
        {
            new(itemId, AnalysisMode.Credits, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray),
            new(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, EntrypointTestHelpers.EmptyJsonArray, 100.5, 0),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Keyframe, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.BlackFrame, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
        };

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            db.DetectionCache.AddRange(entries);
            await db.SaveChangesAsync();
        }

        var deleted = await DatabaseTestHelpers.CreateCacheDatabase(scope.CacheDbPath).DeleteByModeAsync(mode);

        Assert.Equal(entries.Count(e => e.Mode == mode), deleted);
        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            Assert.False(db.DetectionCache.Any(e => e.ItemId == itemId && e.Mode == mode));
            Assert.Equal(entries.Count(e => e.Mode != mode), db.DetectionCache.Count(e => e.ItemId == itemId));
        }
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
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var range = new TimeRange(0, 30);

        // The cache row must be Brotli-compressed because DetectionCacheService.TryRead decompresses DB payloads.
        var compressedEmpty = DetectionCacheService.CompressBrotli(Array.Empty<TimeRange>());

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId,
                AnalysisMode.Introduction,
                CacheEntryType.Silence,
                compressedEmpty,
                range.Start,
                range.End));
            await db.SaveChangesAsync();
        }

        TimeRange[] result;
        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            // If the empty-array bug were present this would throw FingerprintException (file not found).
            result = await cachingScope.CreateFFmpegService().DetectSilenceAsync(episode, range, AnalysisMode.Introduction);
        }

        Assert.Empty(result);
    }

    [Fact]
    public void StreamScopedChromaprintCache_AcceptsMatchingLegacyDefaultHash()
    {
        var episodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        uint[] fingerprint = [111u, 222u];

        using var cachingScope = new CachingPluginScope(cacheDir);
        var config = Plugin.Instance!.Configuration;
        var legacyHash = ConfigHasher.LegacyChromaprintCacheWithoutLanguage(config, AnalysisMode.Introduction);

        DatabaseTestHelpers.CreateCacheDatabase(cachingScope.CacheDbPath).Upsert(
            episodeId,
            AnalysisMode.Introduction,
            CacheEntryType.Chromaprint,
            0,
            600,
            DetectionCacheService.CompressBrotli(fingerprint),
            legacyHash);

        Assert.True(cachingScope.CacheService.TryRead(
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
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var intervals = new BlackInterval[] { new(10, 20), new(30.5, 35) };
        var compressed = DetectionCacheService.CompressBrotli(intervals);

        string cacheDbPath;
        using (var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            using var db = new DetectionCacheDbContext(scope.CacheDbPath);
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId,
                AnalysisMode.Credits,
                CacheEntryType.BlackInterval,
                compressed,
                1560,
                1800));
            await db.SaveChangesAsync();
        }

        BlackInterval[] result;
        using (var cachingScope = new CachingPluginScope(cacheDir, cacheDbPath))
        {
            result = await cachingScope.CreateFFmpegService()
                .DetectBlackIntervalsAsync(episode, new TimeRange(1560, 1800), 32, 85);
        }

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
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        // Pre-populate the DB with a fingerprint at the correct start/end.
        var fingerprint = new uint[] { 111u, 222u, 333u };
        var compressed = DetectionCacheService.CompressBrotli(fingerprint);

        string cacheDbPath;
        using (var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            using var db = new DetectionCacheDbContext(scope.CacheDbPath);
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId,
                AnalysisMode.Introduction,
                CacheEntryType.Chromaprint,
                compressed,
                0,        // start
                600));    // end = IntroFingerprintEnd
            await db.SaveChangesAsync();
        }

        uint[] result;
        using (var cachingScope = new CachingPluginScope(cacheDir, cacheDbPath))
        {
            // Should hit cache because start=0, end=600 matches
            result = await cachingScope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Introduction);
        }

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
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var compressed = DetectionCacheService.CompressBrotli(new uint[] { 111u, 222u, 333u });

        string cacheDbPath;
        using (var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            using var db = new DetectionCacheDbContext(scope.CacheDbPath);
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId,
                AnalysisMode.Introduction,
                CacheEntryType.Chromaprint,
                compressed,
                0,
                600));
            await db.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var cachingScope = new CachingPluginScope(cacheDir, cacheDbPath);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cachingScope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Introduction, cts.Token));
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
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        if (seedRow)
        {
            using var db = new DetectionCacheDbContext(scope.CacheDbPath);
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId,
                mode,
                CacheEntryType.Chromaprint,
                EntrypointTestHelpers.EmptyJsonArray,
                rowStart,
                rowEnd,
                legacyHash ? ConfigHasher.LegacyChromaprintCacheWithoutLanguage(new PluginConfiguration(), mode) : string.Empty));
            db.SaveChanges();
        }

        using var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath);
        Assert.Equal(expected, cachingScope.CacheService.HasCachedFingerprint(episode, mode));
    }

    [Fact]
    public async Task DeleteUnreadableEntriesAsync_DeletesOnlyRowsNoReadPathAccepts()
    {
        var itemId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var cachingScope = new CachingPluginScope(cacheDir);
        var config = Plugin.Instance!.Configuration;
        var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cachingScope.CacheDbPath);

        // One row per acceptance path, distinguished by their range keys, plus one row whose
        // hash no read path accepts any more (e.g. written by the intermediate release that
        // suffixed the legacy Chromaprint hash input with empty audio tokens).
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 100, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.DetectionCache(config, CacheEntryType.Chromaprint, AnalysisMode.Introduction));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 200, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.LegacyChromaprintCacheWithoutLanguage(config, AnalysisMode.Introduction));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 300, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.DetectionCache(config, CacheEntryType.Chromaprint, AnalysisMode.Introduction, MostChannelsStreamCacheVariant));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 400, EntrypointTestHelpers.EmptyJsonArray, string.Empty);
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, 0, 500, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.DetectionCache(config, CacheEntryType.Silence, AnalysisMode.Introduction));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 600, EntrypointTestHelpers.EmptyJsonArray, "0123456789ABCDEF");

        var deleted = await cachingScope.CacheService.DeleteUnreadableEntriesAsync();

        Assert.Equal(1, deleted);
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 100));
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 200));
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 300));
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 400));
        Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, 0, 500));
        Assert.Null(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 600));
    }

    /// <summary>
    /// Sets up Plugin.Instance with a real cache dir and CacheFingerprints enabled.
    /// </summary>
    private sealed class CachingPluginScope : IDisposable
    {
        private readonly EntrypointTestHelpers.PluginInstanceScope _inner;

        public CachingPluginScope(string cacheDir, string? cacheDbPath = null)
        {
            _inner = new EntrypointTestHelpers.PluginInstanceScope(cacheDir, cacheDbPath);
            var plugin = Plugin.Instance;
            if (plugin is not null)
            {
                EntrypointTestHelpers.SetPropertyOrField(
                    plugin,
                    "Configuration",
                    new PluginConfiguration { CacheFingerprints = true });
            }

            CacheService = DatabaseTestHelpers.CreateCacheService(_inner.CacheDbPath);
        }

        public DetectionCacheService CacheService { get; }

        public string CacheDbPath => _inner.CacheDbPath;

        public FFmpegService CreateFFmpegService()
        {
            return new FFmpegService(NullLogger<FFmpegService>.Instance, CacheService);
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }
}
