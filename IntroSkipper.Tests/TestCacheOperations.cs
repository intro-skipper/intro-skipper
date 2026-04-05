// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Xunit;

public sealed class TestCacheOperations
{
    private static readonly byte[] EmptyPayload = Encoding.UTF8.GetBytes("[]");

    [Fact]
    public void DeleteCacheFiles_Introduction_DeletesIntroFilesOnly()
    {
        var itemId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        // Entries that should be kept (Credits mode)
        var shouldKeep = new DbDetectionCache[]
        {
            new(itemId, AnalysisMode.Credits, CacheEntryType.Chromaprint, EmptyPayload),
            new(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, EmptyPayload, 100.5, 0),
        };

        // Entries that should be deleted (Introduction mode)
        var shouldDelete = new DbDetectionCache[]
        {
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EmptyPayload),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, EmptyPayload, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Keyframe, EmptyPayload, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.BlackFrame, EmptyPayload, 0, 30),
        };

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = Plugin.CreateCacheDbContext())
        {
            db.DetectionCache.AddRange(shouldKeep);
            db.DetectionCache.AddRange(shouldDelete);
            db.SaveChanges();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            FFmpegWrapper.DeleteCacheFiles(AnalysisMode.Introduction);
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
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EmptyPayload),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Silence, EmptyPayload, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.Keyframe, EmptyPayload, 0, 30),
            new(itemId, AnalysisMode.Introduction, CacheEntryType.BlackFrame, EmptyPayload, 0, 30),
        };

        // Entries that should be deleted (Credits mode)
        var shouldDelete = new DbDetectionCache[]
        {
            new(itemId, AnalysisMode.Credits, CacheEntryType.Chromaprint, EmptyPayload),
            new(itemId, AnalysisMode.Credits, CacheEntryType.BlackFrame, EmptyPayload, 100.5, 0),
        };

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = Plugin.CreateCacheDbContext())
        {
            db.DetectionCache.AddRange(shouldKeep);
            db.DetectionCache.AddRange(shouldDelete);
            db.SaveChanges();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            FFmpegWrapper.DeleteCacheFiles(AnalysisMode.Credits);
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
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EmptyPayload));
            db.SaveChanges();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(FFmpegWrapper.HasCachedFingerprint(episode, AnalysisMode.Introduction));
        }
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsTrueForLegacyFile()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        // Legacy path: just the episode ID, no suffix (text format on disk)
        File.WriteAllText(cacheDir + Path.DirectorySeparatorChar + episode.EpisodeId.ToString("N"), "x");

        using var _ = new CachingPluginScope(cacheDir);
        Assert.True(FFmpegWrapper.HasCachedFingerprint(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsFalseWhenNoFile()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var _ = new CachingPluginScope(cacheDir);
        Assert.False(FFmpegWrapper.HasCachedFingerprint(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public void LegacyFingerprintCache_MigratedToDbAndDeleted()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 60,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var id = episode.EpisodeId.ToString("N");
        var legacyPath = Path.Combine(cacheDir, Path.GetFileName(id));

        // Write a valid legacy text-format fingerprint (one uint per line)
        uint[] expectedFingerprint = [1234567890u, 987654321u, 42u];
        File.WriteAllLines(
            legacyPath,
            Array.ConvertAll(expectedFingerprint, v => v.ToString(CultureInfo.InvariantCulture)));

        uint[] result;
        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            result = FFmpegWrapper.Fingerprint(episode, AnalysisMode.Introduction);
        }

        Assert.False(File.Exists(legacyPath), "Legacy text cache file should be deleted after migration");

        // Use DetectionCacheDbContext directly since Plugin.Instance is no longer set after scope disposal.
        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.True(
            db.DetectionCache.Any(e =>
                e.ItemId == episode.EpisodeId &&
                e.Mode == AnalysisMode.Introduction &&
                e.Type == CacheEntryType.Chromaprint),
            "DB row should be created after migration");

        // Verify the fingerprint was correctly round-tripped.
        var migrated = ReadFingerprintFromDb(db, episode.EpisodeId, AnalysisMode.Introduction);
        Assert.Equal(expectedFingerprint, result);
        Assert.Equal(expectedFingerprint, migrated);
    }

    [Fact]
    public void CorruptLegacyFingerprintCache_DeletedAndReanalyzed()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 60,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var id = episode.EpisodeId.ToString("N");
        var legacyPath = Path.Combine(cacheDir, id);

        // Write a corrupt legacy file (binary content, not parseable as uint lines)
        File.WriteAllBytes(legacyPath, [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE]);

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;

            // Fingerprint() will fail because the path doesn't exist, but that's fine —
            // we only care that the corrupt legacy file was deleted and no DB row was written.
            try { FFmpegWrapper.Fingerprint(episode, AnalysisMode.Introduction); }
            catch (FingerprintException ex)
            {
                // Expected in this test for non-existent paths; ignore and verify side effects instead.
                _ = ex;
            }
        }

        Assert.False(File.Exists(legacyPath), "Corrupt legacy cache file should be deleted");

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.False(
            db.DetectionCache.Any(e =>
                e.ItemId == episode.EpisodeId &&
                e.Mode == AnalysisMode.Introduction &&
                e.Type == CacheEntryType.Chromaprint),
            "No DB row should be written for a corrupt legacy file");
    }

    /// <summary>
    /// Regression test: a cached empty array ("[]") must be treated as a valid cache hit.
    /// Before the fix, TryReadJsonCache returned false for empty arrays, causing unnecessary re-analysis.
    /// </summary>
    [Fact]
    public void EmptyArrayCacheEntry_TreatedAsCacheHit()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var range = new TimeRange(0, 30);

        // The cache row must be Brotli-compressed because TryReadJsonCache calls DecompressBrotli.
        var compressedEmpty = FFmpegWrapper.CompressBrotli(JsonSerializer.SerializeToUtf8Bytes(Array.Empty<TimeRange>()));

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
            db.SaveChanges();
        }

        TimeRange[] result;
        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            // If the empty-array bug were present this would throw FingerprintException (file not found).
            result = FFmpegWrapper.DetectSilence(episode, range);
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

    private static uint[] ReadFingerprintFromDb(DetectionCacheDbContext db, Guid itemId, AnalysisMode mode)
    {
        var entry = db.DetectionCache.FirstOrDefault(e =>
            e.ItemId == itemId &&
            e.Mode == mode &&
            e.Type == CacheEntryType.Chromaprint);

        if (entry is null)
        {
            return [];
        }

        var json = FFmpegWrapper.DecompressBrotli(entry.Data);
        return JsonSerializer.Deserialize<uint[]>(json) ?? [];
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
        }

        public string CacheDbPath => _inner.CacheDbPath;

        public void Dispose() => _inner.Dispose();
    }
}
