// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
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

        using (var db = Plugin.CreateCacheDbContext())
        {
            db.DetectionCache.AddRange(shouldKeep);
            db.DetectionCache.AddRange(shouldDelete);
            db.SaveChanges();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            cachingScope.CacheService.DeleteByMode(AnalysisMode.Introduction);
        }

        using (var db = Plugin.CreateCacheDbContext())
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

        using (var db = Plugin.CreateCacheDbContext())
        {
            db.DetectionCache.AddRange(shouldKeep);
            db.DetectionCache.AddRange(shouldDelete);
            db.SaveChanges();
        }

        using (var cachingScope = new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            cachingScope.CacheService.DeleteByMode(AnalysisMode.Credits);
        }

        using (var db = Plugin.CreateCacheDbContext())
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

        using (var db = Plugin.CreateCacheDbContext())
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

        using (var db = Plugin.CreateCacheDbContext())
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
        var original = new BlackFrame[] {
            new(85, 12.345, 300),
            new(100, 0.0, 1),
            new(0, 999.999, 99999),
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<BlackFrame[]>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Length, deserialized.Length);
        for (var i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].Percentage, deserialized[i].Percentage);
            Assert.Equal(original[i].Time, deserialized[i].Time);
            Assert.Equal(original[i].Frame, deserialized[i].Frame);
        }
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
            using var db = Plugin.CreateCacheDbContext();
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
            using var db = Plugin.CreateCacheDbContext();
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId,
                AnalysisMode.Introduction,
                CacheEntryType.Chromaprint,
                compressed,
                0,
                600));
            await db.SaveChangesAsync();
        }

        using var cts = new System.Threading.CancellationTokenSource();
        await cts.CancelAsync();

        using var cachingScope = new CachingPluginScope(cacheDir, cacheDbPath);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cachingScope.CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Introduction, cts.Token));
    }

    [Fact]
    public async Task CachedFingerprint_MissesOnDifferentEnd()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
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
            using var db = Plugin.CreateCacheDbContext();
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
            // Should miss cache (end mismatch: 600 vs 900) and then throw
            // because the file doesn't actually exist for ffmpeg
            var svc = cachingScope.CreateFFmpegService();
            await Assert.ThrowsAsync<FingerprintException>(
                () => svc.FingerprintAsync(episode, AnalysisMode.Introduction));
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

        using (var db = Plugin.CreateCacheDbContext())
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

        using (var db = Plugin.CreateCacheDbContext())
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

        using (var db = Plugin.CreateCacheDbContext())
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

            CacheService = new DetectionCacheService(
                NullLogger<DetectionCacheService>.Instance);
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
