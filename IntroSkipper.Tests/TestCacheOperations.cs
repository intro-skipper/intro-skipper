// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
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
    [Fact]
    public void DeleteCacheFiles_Introduction_DeletesIntroFilesOnly()
    {
        var itemId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        // Entries that should be kept (Credits mode)
        var shouldKeep = new DbDetectionCache[]
        {
            new(itemId, AnalysisMode.Credits, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray),
            new(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, EntrypointTestHelpers.EmptyJsonArray, 100.5, 0),
        };

        // Entries that should be deleted (Introduction mode)
        var shouldDelete = new DbDetectionCache[]
        {
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Keyframe, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.BlackFrame, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
        };

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            db.DetectionCache.AddRange(shouldKeep);
            db.DetectionCache.AddRange(shouldDelete);
            db.SaveChanges();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            cachingScope.CacheService.DeleteByMode(AnalysisMode.Introduction);
        }

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            // Introduction entries should be gone
            Assert.False(
                db.DetectionCache.Any(e => e.ItemId == itemId && e.Mode == AnalysisMode.Introduction),
                "All Introduction cache entries should be deleted");

            // Credits entries should remain
            Assert.Equal(
                shouldKeep.Length,
                db.DetectionCache.Count(e => e.ItemId == itemId && e.Mode == AnalysisMode.Credits));
        }
    }

    [Fact]
    public void DeleteCacheFiles_Credits_DeletesCreditsFilesOnly()
    {
        var itemId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        // Entries that should be kept (Introduction mode)
        var shouldKeep = new DbDetectionCache[]
        {
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Keyframe, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.BlackFrame, EntrypointTestHelpers.EmptyJsonArray, 0, 30),
        };

        // Entries that should be deleted (Credits mode)
        var shouldDelete = new DbDetectionCache[]
        {
            new(itemId, AnalysisMode.Credits, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray),
            new(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, EntrypointTestHelpers.EmptyJsonArray, 100.5, 0),
        };

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            db.DetectionCache.AddRange(shouldKeep);
            db.DetectionCache.AddRange(shouldDelete);
            db.SaveChanges();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            cachingScope.CacheService.DeleteByMode(AnalysisMode.Credits);
        }

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            // Credits entries should be gone
            Assert.False(
                db.DetectionCache.Any(e => e.ItemId == itemId && e.Mode == AnalysisMode.Credits),
                "All Credits cache entries should be deleted");

            // Introduction entries should remain
            Assert.Equal(
                shouldKeep.Length,
                db.DetectionCache.Count(e => e.ItemId == itemId && e.Mode == AnalysisMode.Introduction));
        }
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsTrueForDbRow()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
            db.SaveChanges();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(cachingScope.CacheService.HasCachedFingerprint(episode, AnalysisMode.Introduction));
        }
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsFalseWhenNoFile()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var cachingScope = new CachingPluginScope(cacheDir);
        Assert.False(cachingScope.CacheService.HasCachedFingerprint(episode, AnalysisMode.Introduction));
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

    /// <summary>
    /// Verifies that <see cref="BlackFrame"/> round-trips correctly through JSON serialization.
    /// This ensures the positional record deserialization works for the cache layer.
    /// </summary>
    [Fact]
    public void BlackFrame_JsonRoundTrip_PreservesAllFields()
    {
        BlackFrame[] original =
        [
            new(85, 12.345, 300),
            new(100, 0.0, 1),
            new(0, 999.999, 99999),
        ];

        AssertJsonRoundTrips(original);
    }

    [Fact]
    public void BlackInterval_JsonRoundTrip_PreservesAllFields()
    {
        BlackInterval[] original =
        [
            new(12.345, 67.89),
            new(0, 1.25),
        ];

        AssertJsonRoundTrips(original);
    }

    [Fact]
    public void DetectionCacheHash_BlackFrame_ChangesWithThreshold()
    {
        var baseline = new PluginConfiguration { BlackFrameThreshold = 32 };
        var changed = new PluginConfiguration { BlackFrameThreshold = 64 };

        Assert.NotEqual(
            ConfigHasher.DetectionCache(baseline, CacheEntryType.BlackFrame, AnalysisMode.Credits),
            ConfigHasher.DetectionCache(changed, CacheEntryType.BlackFrame, AnalysisMode.Credits));
    }

    [Fact]
    public void DetectionCacheHash_BlackFrame_ChangesWithMode()
    {
        var config = new PluginConfiguration { BlackFrameThreshold = 32 };

        Assert.NotEqual(
            ConfigHasher.DetectionCache(config, CacheEntryType.BlackFrame, AnalysisMode.Introduction),
            ConfigHasher.DetectionCache(config, CacheEntryType.BlackFrame, AnalysisMode.Credits));
    }

    [Fact]
    public void DetectionCacheHash_BlackFrame_IgnoresMinimumPercentage()
    {
        var baseline = new PluginConfiguration { BlackFrameThreshold = 32, BlackFrameMinimumPercentage = 85 };
        var changed = new PluginConfiguration { BlackFrameThreshold = 32, BlackFrameMinimumPercentage = 95 };

        Assert.Equal(
            ConfigHasher.DetectionCache(baseline, CacheEntryType.BlackFrame, AnalysisMode.Credits),
            ConfigHasher.DetectionCache(changed, CacheEntryType.BlackFrame, AnalysisMode.Credits));
    }

    [Fact]
    public void DetectionCacheHash_BlackInterval_ChangesWithThreshold()
    {
        var baseline = new PluginConfiguration { BlackFrameThreshold = 32 };
        var changed = new PluginConfiguration { BlackFrameThreshold = 64 };

        Assert.NotEqual(
            ConfigHasher.DetectionCache(baseline, CacheEntryType.BlackInterval, AnalysisMode.Credits),
            ConfigHasher.DetectionCache(changed, CacheEntryType.BlackInterval, AnalysisMode.Credits));
    }

    [Fact]
    public void DetectionCacheHash_BlackInterval_ChangesWithMode()
    {
        var config = new PluginConfiguration { BlackFrameThreshold = 32 };

        Assert.NotEqual(
            ConfigHasher.DetectionCache(config, CacheEntryType.BlackInterval, AnalysisMode.Introduction),
            ConfigHasher.DetectionCache(config, CacheEntryType.BlackInterval, AnalysisMode.Credits));
    }

    [Fact]
    public void DetectionCacheHash_BlackInterval_DiffersFromBlackFrame()
    {
        var config = new PluginConfiguration { BlackFrameThreshold = 32 };

        Assert.NotEqual(
            ConfigHasher.DetectionCache(config, CacheEntryType.BlackFrame, AnalysisMode.Credits),
            ConfigHasher.DetectionCache(config, CacheEntryType.BlackInterval, AnalysisMode.Credits));
    }

    [Fact]
    public void DetectionCacheHash_BlackInterval_VariesWithMinimumPercentage()
    {
        // BlackFrameMinimumPercentage is passed to blackdetect as pic_th and is baked into the cached
        // intervals, so changing it must invalidate the BlackInterval detection cache.
        var baseline = new PluginConfiguration { BlackFrameThreshold = 32, BlackFrameMinimumPercentage = 85 };
        var changed = new PluginConfiguration { BlackFrameThreshold = 32, BlackFrameMinimumPercentage = 95 };

        Assert.NotEqual(
            ConfigHasher.DetectionCache(baseline, CacheEntryType.BlackInterval, AnalysisMode.Credits),
            ConfigHasher.DetectionCache(changed, CacheEntryType.BlackInterval, AnalysisMode.Credits));
    }


    [Fact]
    public void AnalysisHash_Credits_ChangesWithDetectNonBlackCredits()
    {
        var baseline = new PluginConfiguration { UseAlternativeBlackFrameAnalyzer = true, DetectNonBlackCredits = true };
        var changed = new PluginConfiguration { UseAlternativeBlackFrameAnalyzer = true, DetectNonBlackCredits = false };

        // Toggling the non-black fallback changes credits output (when its analyzer is active), so it
        // must invalidate stored credits analysis instead of hash-matching a stale result.
        Assert.NotEqual(
            ConfigHasher.Analysis(baseline, AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(changed, AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: true));
    }

    [Fact]
    public void AnalysisHash_Credits_IgnoresDetectNonBlackCredits_WhenAlternativeAnalyzerOff()
    {
        var baseline = new PluginConfiguration { UseAlternativeBlackFrameAnalyzer = false, DetectNonBlackCredits = true };
        var changed = new PluginConfiguration { UseAlternativeBlackFrameAnalyzer = false, DetectNonBlackCredits = false };

        // The default BlackFrameAnalyzer cannot observe DetectNonBlackCredits, so toggling it must not
        // invalidate stored credits analysis on that path.
        Assert.Equal(
            ConfigHasher.Analysis(baseline, AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(changed, AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: true));
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

    [FactSkipFFmpegTests]
    public async Task CachedFingerprint_MissesOnDifferentEnd()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "../../../audio/big_buck_bunny_intro.mp3",
            IntroFingerprintEnd = 900, // current setting wants 900s
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        // Pre-populate DB with a fingerprint cached at old setting (end=600)
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
                0,      // start
                600));  // old end
            await db.SaveChangesAsync();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, cacheDbPath))
        {
            var result = await cachingScope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Introduction);

            Assert.NotEqual(fingerprint, result);
            Assert.NotEmpty(result);
        }
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsFalseForStaleEntry()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            IntroFingerprintEnd = 900, // current setting
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            // Stale entry: cached with old end=600
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray, 0, 600));
            db.SaveChanges();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            // Should return false: DB has end=600 but episode expects end=900
            Assert.False(cachingScope.CacheService.HasCachedFingerprint(episode, AnalysisMode.Introduction));
        }
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsTrueForMatchingEntry()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            IntroFingerprintEnd = 600,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray, 0, 600));
            db.SaveChanges();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(cachingScope.CacheService.HasCachedFingerprint(episode, AnalysisMode.Introduction));
        }
    }

    [Fact]
    public void HasCachedFingerprint_Credits_ReturnsTrueForMatchingEntry()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            CreditsFingerprintStart = 1560,
            Duration = 1800,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = new DetectionCacheDbContext(scope.CacheDbPath))
        {
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray, 1560, 1800));
            db.SaveChanges();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(cachingScope.CacheService.HasCachedFingerprint(episode, AnalysisMode.Credits));
        }
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void CompressBrotli_AllLevels_RoundTripsCorrectly(CompressionLevel level)
    {
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        var plugin = Plugin.Instance;
        Assert.NotNull(plugin);

        EntrypointTestHelpers.SetPropertyOrField(
            plugin,
            "Configuration",
            new PluginConfiguration { CacheCompressionLevel = level });

        uint[] original = [1u, 2u, 3u, 100u, 200u, 42u];
        var compressed = DetectionCacheService.CompressBrotli(original);
        var decompressed = DetectionCacheService.DecompressBrotli<uint[]>(compressed);

        Assert.NotNull(decompressed);
        Assert.Equal(original, decompressed);
    }

    private static void AssertJsonRoundTrips<T>(T[] expected)
    {
        var json = JsonSerializer.Serialize(expected);
        var actual = JsonSerializer.Deserialize<T[]>(json);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
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
