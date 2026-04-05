// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
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
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
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

        // Write a legacy fingerprint with line count matching ~60s of audio.
        // InferDuration(lineCount) must be within 5s of 60.
        // (60 - 2.6) / SampleDuration ≈ 463 lines → InferDuration(463) ≈ 60
        var lineCount = (int)Math.Round((60.0 - ChromaprintConstants.HashWindowDuration) / ChromaprintConstants.SampleDuration);
        uint[] expectedFingerprint = new uint[lineCount];
        for (var i = 0; i < lineCount; i++)
        {
            expectedFingerprint[i] = (uint)(i + 1);
        }

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

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.True(
            db.DetectionCache.Any(e =>
                e.ItemId == episode.EpisodeId &&
                e.Mode == AnalysisMode.Introduction &&
                e.Type == CacheEntryType.Chromaprint),
            "DB row should be created after migration");

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
            catch (FingerprintException)
            {
                // Expected in this test for non-existent paths; ignore and verify side effects instead.
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

    [Fact]
    public void CachedFingerprint_StoresRealStartEnd()
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
        var compressed = FFmpegWrapper.CompressBrotli(
            JsonSerializer.SerializeToUtf8Bytes(fingerprint));

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
            db.SaveChanges();
        }

        uint[] result;
        using (new CachingPluginScope(cacheDir, cacheDbPath))
        {
            // Should hit cache because start=0, end=600 matches
            result = FFmpegWrapper.Fingerprint(episode, AnalysisMode.Introduction);
        }

        Assert.Equal(fingerprint, result);
    }

    [Fact]
    public void CachedFingerprint_MissesOnDifferentEnd()
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
        var compressed = FFmpegWrapper.CompressBrotli(
            JsonSerializer.SerializeToUtf8Bytes(fingerprint));

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
            db.SaveChanges();
        }

        using (new CachingPluginScope(cacheDir, cacheDbPath))
        {
            // Should miss cache (end mismatch: 600 vs 900) and then throw
            // because the file doesn't actually exist for ffmpeg
            Assert.Throws<FingerprintException>(
                () => FFmpegWrapper.Fingerprint(episode, AnalysisMode.Introduction));
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
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EmptyPayload, 0, 600));
            db.SaveChanges();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            // Should return false: DB has end=600 but episode expects end=900
            Assert.False(FFmpegWrapper.HasCachedFingerprint(episode, AnalysisMode.Introduction));
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
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EmptyPayload, 0, 600));
            db.SaveChanges();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(FFmpegWrapper.HasCachedFingerprint(episode, AnalysisMode.Introduction));
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
                episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.Chromaprint, EmptyPayload, 1560, 1800));
            db.SaveChanges();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(FFmpegWrapper.HasCachedFingerprint(episode, AnalysisMode.Credits));
        }
    }

    [Fact]
    public void LegacyMigration_Accepted_WhenDurationMatchesCurrentSettings()
    {
        // 4825 lines → InferDuration ≈ 600s. Episode expects end=600 → |600-600| <= 5 → accept.
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 600,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var id = episode.EpisodeId.ToString("N");
        var legacyPath = Path.Combine(cacheDir, id);

        // Write 4825 fingerprint lines to simulate a 10-minute fingerprint
        var lines = new string[4825];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = ((uint)(i * 12345)).ToString(CultureInfo.InvariantCulture);
        }

        File.WriteAllLines(legacyPath, lines);

        uint[] result;
        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            result = FFmpegWrapper.Fingerprint(episode, AnalysisMode.Introduction);
        }

        Assert.False(File.Exists(legacyPath), "Legacy file should be deleted after successful migration");
        Assert.Equal(4825, result.Length);

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var entry = db.DetectionCache.FirstOrDefault(e =>
            e.ItemId == episode.EpisodeId &&
            e.Mode == AnalysisMode.Introduction &&
            e.Type == CacheEntryType.Chromaprint);
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Start);
        Assert.Equal(600, entry.End);
    }

    [Fact]
    public void LegacyMigration_Rejected_WhenDurationMismatch()
    {
        // 4825 lines → InferDuration ≈ 600s. Episode expects end=900 → |600-900| = 300 > 5 → reject.
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 900,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var id = episode.EpisodeId.ToString("N");
        var legacyPath = Path.Combine(cacheDir, id);

        // Write 4825 fingerprint lines (covers ~600s, not the 900s the episode expects)
        var lines = new string[4825];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = ((uint)(i * 12345)).ToString(CultureInfo.InvariantCulture);
        }

        File.WriteAllLines(legacyPath, lines);

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            // Should reject legacy cache and then throw because path doesn't exist for ffmpeg
            Assert.Throws<FingerprintException>(
                () => FFmpegWrapper.Fingerprint(episode, AnalysisMode.Introduction));
        }

        Assert.False(File.Exists(legacyPath), "Legacy file should be deleted even on rejection");

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.False(
            db.DetectionCache.Any(e =>
                e.ItemId == episode.EpisodeId &&
                e.Type == CacheEntryType.Chromaprint),
            "No DB row should be written for rejected legacy file");
    }

    [Fact]
    public void LegacyMigration_Credits_AcceptedWithCorrectStartEnd()
    {
        // Credits: start=1560, end=1800, duration=240s
        // Need ~1916 lines: (240 - 2.6) / 0.12383 ≈ 1916 lines → InferDuration ≈ 240
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            CreditsFingerprintStart = 1560,
            Duration = 1800,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var id = episode.EpisodeId.ToString("N");
        var legacyPath = Path.Combine(cacheDir, id + "-credits");

        // Compute line count that produces ~240s
        // 240 - 2.6 = 237.4 / 0.12383 ≈ 1917
        var lineCount = (int)Math.Round((240.0 - ChromaprintConstants.HashWindowDuration) / ChromaprintConstants.SampleDuration);
        var lines = new string[lineCount];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = ((uint)(i * 54321)).ToString(CultureInfo.InvariantCulture);
        }

        File.WriteAllLines(legacyPath, lines);

        uint[] result;
        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            result = FFmpegWrapper.Fingerprint(episode, AnalysisMode.Credits);
        }

        Assert.False(File.Exists(legacyPath));
        Assert.Equal(lineCount, result.Length);

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var entry = db.DetectionCache.FirstOrDefault(e =>
            e.ItemId == episode.EpisodeId &&
            e.Mode == AnalysisMode.Credits &&
            e.Type == CacheEntryType.Chromaprint);
        Assert.NotNull(entry);
        Assert.Equal(1560, entry.Start);
        Assert.Equal(1800, entry.End);
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
