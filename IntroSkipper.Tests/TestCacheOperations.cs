// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
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
    public async Task CachedFingerprint_RecapReadsIntroductionRow()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 600,
        };
        var fingerprint = new uint[] { 111u, 222u, 333u };
        using var scope = new CachingPluginScope();
        scope.SeedRow(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, DetectionCacheService.CompressBrotli(fingerprint), 0, 600);

        var result = await scope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Recap);

        Assert.Equal(fingerprint, result);
    }

    /// <summary>
    /// Upgrade scenario. A release that cached Recap under its own key fingerprinted this
    /// episode with intro scanning disabled, so only a Recap row exists. The read must serve
    /// it and copy it under the shared Introduction key so later reads take the fast path.
    /// </summary>
    [Fact]
    public async Task CachedFingerprint_RecapReadsAndUpgradesRecapOnlyRow()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 600,
        };
        var fingerprint = new uint[] { 111u, 222u, 333u };
        using var scope = new CachingPluginScope();
        scope.SeedRow(episode.EpisodeId, AnalysisMode.Recap, CacheEntryType.Chromaprint, DetectionCacheService.CompressBrotli(fingerprint), 0, 600, scope.LegacyHash(AnalysisMode.Recap));

        var result = await scope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Recap);

        Assert.Equal(fingerprint, result);
        var upgraded = scope.CacheDatabase.FindEntry(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 600);
        Assert.NotNull(upgraded);
        Assert.Equal(fingerprint, DetectionCacheService.DecompressBrotli<uint[]>(upgraded.Data));
        Assert.Equal(fingerprint, await scope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public async Task CachedFingerprint_ReadsPreStreamSelectionRow()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 600,
        };
        var fingerprint = new uint[] { 111u, 222u, 333u };
        using var scope = new CachingPluginScope();

        // Row written by a release without audio stream selection. The default configuration
        // still fingerprints FFmpeg's default stream, so the row is reusable as-is.
        scope.SeedRow(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, DetectionCacheService.CompressBrotli(fingerprint), 0, 600, scope.LegacyHash(AnalysisMode.Introduction));

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
    // over 1560-1800 s; a row counts only when its range matches. Recap reads the
    // Introduction row, so the seeded mode is Introduction for the Recap case.
    [Theory]
    [InlineData(AnalysisMode.Introduction, false, 0, 0, false)]
    [InlineData(AnalysisMode.Introduction, true, 0, 900, false)]
    [InlineData(AnalysisMode.Introduction, true, 0, 600, true)]
    [InlineData(AnalysisMode.Credits, true, 1560, 1800, true)]
    [InlineData(AnalysisMode.Recap, true, 0, 600, true)]
    public void HasCachedFingerprint_MatchesRowsByRange(AnalysisMode mode, bool seedRow, double rowStart, double rowEnd, bool expected)
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
            scope.SeedRow(episode.EpisodeId, QueuedEpisode.FingerprintCacheMode(mode), CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray, rowStart, rowEnd);
        }

        Assert.Equal(expected, scope.CacheService.HasCachedFingerprint(episode, mode));
    }

    // The Recap case covers an episode fingerprinted by a release that cached Recap under its
    // own key with no Introduction row; it must stay in the Recap comparison pool.
    [Theory]
    [InlineData(AnalysisMode.Introduction, AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Recap, AnalysisMode.Recap)]
    public void HasCachedFingerprint_AcceptsPreStreamSelectionRow(AnalysisMode mode, AnalysisMode rowMode)
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), IntroFingerprintEnd = 600 };
        using var scope = new CachingPluginScope();
        scope.SeedRow(episode.EpisodeId, rowMode, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray, 0, 600, scope.LegacyHash(rowMode));

        Assert.True(scope.CacheService.HasCachedFingerprint(episode, mode));
    }

    /// <summary>
    /// Upgrade scenario. Episodes 1 and 2 were analyzed by a release without audio stream
    /// selection, so their fingerprint rows carry the legacy hash. New episodes 3 and 4 each
    /// share an intro with one of them and not with each other, so both intros are found only
    /// if the analyzed episodes stay in the comparison pool and their legacy rows are read.
    /// Rejecting the rows would leave the new episodes unanalyzed without refingerprinting anyone.
    /// </summary>
    [Fact]
    public async Task AnalyzeMediaFiles_ComparesNewEpisodesAgainstPreStreamSelectionFingerprints()
    {
        using var scope = new CachingPluginScope();
        var legacyHash = scope.LegacyHash(AnalysisMode.Introduction);
        var currentHash = ConfigHasher.DetectionCache(Plugin.Instance!.Configuration, CacheEntryType.Chromaprint, AnalysisMode.Introduction, MostChannelsStreamCacheVariant);

        // Twenty shared intro points (about 2.5 s) followed by twenty points unique to the episode.
        static uint[] Points(uint intro, uint unique)
            => [.. Enumerable.Range(0, 20).Select(i => intro + (uint)i), .. Enumerable.Range(0, 20).Select(i => unique + (uint)i)];

        var episodes = new List<QueuedEpisode>();
        foreach (var (number, intro, unique, analyzed) in new[] { (1, 0x1000u, 0x2000u, true), (2, 0x5000u, 0x6000u, true), (3, 0x1000u, 0x3000u, false), (4, 0x5000u, 0x7000u, false) })
        {
            var episode = new QueuedEpisode
            {
                EpisodeId = Guid.NewGuid(),
                EpisodeNumber = number,
                Path = "/does/not/exist.mkv",
                IntroFingerprintEnd = 600,
                Duration = 1800,
            };
            if (analyzed)
            {
                episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.Analyzed);
            }

            // Every fingerprint is served from the cache so no ffmpeg runs; only the hash
            // distinguishes pre-upgrade rows from rows this release wrote.
            scope.SeedRow(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, DetectionCacheService.CompressBrotli(Points(intro, unique)), 0, 600, analyzed ? legacyHash : currentHash);
            episodes.Add(episode);
        }

        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var analyzer = new ChromaprintAnalyzer(
            NullLogger<ChromaprintAnalyzer>.Instance,
            scope.CreateFFmpegService(),
            scope.CacheService,
            database,
            new PluginConfiguration
            {
                MinimumIntroDuration = 1,
                MaximumFingerprintPointDifferences = 0,
                MaximumTimeSkip = 0.2,
                InvertedIndexShift = 0,
                AdjustIntroBasedOnChapters = false,
                AdjustIntroBasedOnSilence = false,
                SnapToKeyframe = false,
            });

        await analyzer.AnalyzeMediaFiles(episodes, AnalysisMode.Introduction, CancellationToken.None);

        foreach (var episode in episodes.Where(e => e.EpisodeNumber >= 3))
        {
            Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
            Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
        }
    }

    [Fact]
    public async Task DeleteUnreadableEntriesAsync_DeletesOnlyRowsNoReadPathAccepts()
    {
        var itemId = Guid.NewGuid();
        using var scope = new CachingPluginScope();
        var config = Plugin.Instance!.Configuration;
        var cacheDatabase = scope.CacheDatabase;

        // One row per acceptance path, distinguished by their range keys, plus one row whose
        // hash no read path accepts.
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 100, EntrypointTestHelpers.EmptyJsonArray, ConfigHasher.DetectionCache(config, CacheEntryType.Chromaprint, AnalysisMode.Introduction));
        cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 200, EntrypointTestHelpers.EmptyJsonArray, scope.LegacyHash(AnalysisMode.Introduction));
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
        /// The hash a release without audio stream selection wrote on this configuration's
        /// Chromaprint rows.
        /// </summary>
        public string LegacyHash(AnalysisMode mode)
            => ConfigHasher.LegacyChromaprintCacheWithoutLanguage(Plugin.Instance!.Configuration, mode);

        /// <summary>
        /// Stores one raw cache row. Pass the bytes a read path expects (Brotli for
        /// payloads the service decompresses).
        /// </summary>
        public void SeedRow(Guid itemId, AnalysisMode mode, CacheEntryType type, byte[] data, double start = 0, double end = 0, string configHash = "")
            => CacheDatabase.Upsert(itemId, mode, type, start, end, data, configHash);

        public void Dispose() => _inner.Dispose();
    }
}
