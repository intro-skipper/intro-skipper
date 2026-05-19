// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only


using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestCacheOperations
{
    [Fact]
    public async Task DeleteCacheFiles_Introduction_DeletesIntroFilesOnly()
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
            await db.SaveChangesAsync();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            await CreateCacheService().DeleteCacheFilesAsync(AnalysisMode.Introduction);
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
    public async Task DeleteCacheFiles_Credits_DeletesCreditsFilesOnly()
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
            await db.SaveChangesAsync();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            await CreateCacheService().DeleteCacheFilesAsync(AnalysisMode.Credits);
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
    public async Task DeleteStaleCachesAsync_DeletesBareGuidLegacyFingerprintFileWhenItemDisabled()
    {
        var staleId = Guid.NewGuid();
        var retainedId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var stalePath = Path.Join(cacheDir, staleId.ToString("N"));
        var retainedPath = Path.Join(cacheDir, retainedId.ToString("N"));

        await File.WriteAllTextAsync(stalePath, "1");
        await File.WriteAllTextAsync(retainedPath, "1");

        using (new CachingPluginScope(cacheDir))
        {
            await CreateCacheService().DeleteStaleCachesAsync(new HashSet<Guid> { retainedId });
        }

        Assert.False(File.Exists(stalePath));
        Assert.True(File.Exists(retainedPath));
    }

    [Fact]
    public void LegacyDetectionCacheFileName_TryParse_InvalidName_ReturnsNull()
    {
        Assert.Null(LegacyDetectionCacheFileName.TryParse("not-a-guid-silence-1-2-v2"));
    }

    [Fact]
    public void LegacyDetectionCacheFileName_TryParse_UnsupportedSuffix_ReturnsUnsupportedWithItemId()
    {
        var itemId = Guid.NewGuid();

        var unsupported = LegacyDetectionCacheFileName.TryParse($"{itemId:N}-unknown-suffix");

        Assert.NotNull(unsupported);
        Assert.Equal(itemId, unsupported.ItemId);
        Assert.Equal(LegacyDetectionCacheFileName.LegacyCacheKind.Unsupported, unsupported.Kind);
    }

    [Fact]
    public void LegacyDetectionCacheFileName_TryParse_SilenceName_ReturnsParsedRange()
    {
        var itemId = Guid.NewGuid();

        var silence = LegacyDetectionCacheFileName.TryParse($"{itemId:N}-silence-10.5-20.5-v2");

        Assert.NotNull(silence);
        Assert.Equal(itemId, silence.ItemId);
        Assert.Equal(LegacyDetectionCacheFileName.LegacyCacheKind.Silence, silence.Kind);
        Assert.Equal(10.5, silence.Start);
        Assert.Equal(20.5, silence.End);
    }

    [Fact]
    public async Task HasCachedFingerprint_ReturnsTrueForDbRow()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = Plugin.CreateCacheDbContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, DetectionCacheVariant.Chromaprint(), EntrypointTestHelpers.EmptyJsonArray));
            await db.SaveChangesAsync();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(await CreateCacheService().HasCachedFingerprintAsync(episode, AnalysisMode.Introduction));
        }
    }

    [Fact]
    public async Task HasCachedFingerprint_ReturnsFalseForLegacyFileAlone()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        // Legacy path: just the episode ID, no suffix (text format on disk)
        await File.WriteAllTextAsync(cacheDir + Path.DirectorySeparatorChar + episode.EpisodeId.ToString("N"), "x");

        using var _ = new CachingPluginScope(cacheDir);
        Assert.False(await CreateCacheService().HasCachedFingerprintAsync(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public async Task HasCachedFingerprint_ReturnsFalseWhenNoFile()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var _ = new CachingPluginScope(cacheDir);
        Assert.False(await CreateCacheService().HasCachedFingerprintAsync(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public async Task LegacyFingerprintCache_MigratedToDbAndDeleted()
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

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            await CreateCacheService().MigrateLegacyCachesAsync([episode]);
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
        Assert.Equal(expectedFingerprint, migrated);
    }

    [Fact]
    public async Task CorruptLegacyFingerprintCache_DeletedAndReanalyzed()
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
            await CreateCacheService().MigrateLegacyCachesAsync([episode]);
        }

        Assert.True(File.Exists(legacyPath), "Corrupt legacy cache file should be retained for retry or inspection");

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
    public async Task EmptyArrayCacheEntry_TreatedAsCacheHit()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var range = new TimeRange(0, 30);

        // The cache row must be Brotli-compressed because TryReadJsonCache calls DecompressBrotli.
        var compressedEmpty = CreateCacheService().CompressBrotli(Array.Empty<TimeRange>());

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
        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            // If the empty-array bug were present this would throw FingerprintException (file not found).
            result = await CreateDetectionService().DetectSilenceAsync(episode, range, AnalysisMode.Introduction);
        }

        Assert.Empty(result);
    }

    [Fact]
    public async Task DetectSilenceAsync_DoesNotCachePartialOutputWhenFFmpegTimesOut()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/matter.mkv",
        };
        var range = new TimeRange(0, 30);
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            var detectionService = CreateDetectionService(new FixedRunner("silence_start: 0\nsilence_end: 1\n", simulateTimeout: true));

            await Assert.ThrowsAsync<TimeoutException>(() =>
                detectionService.DetectSilenceAsync(episode, range, AnalysisMode.Introduction));
        }

        using var db = new DetectionCacheDbContext(cacheDbPath);
        const double tolerance = 1e-6;
        Assert.False(db.DetectionCache.Any(e =>
            e.ItemId == episode.EpisodeId &&
            e.Mode == AnalysisMode.Introduction &&
            e.Type == CacheEntryType.Silence &&
            Math.Abs(e.Start - range.Start) <= tolerance &&
            Math.Abs(e.End - range.End) <= tolerance));
    }

    [Fact]
    public async Task FingerprintAsync_DoesNotCachePartialOutputWhenFFmpegTimesOut()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/matter.mkv",
            IntroFingerprintEnd = 30,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            var detectionService = CreateDetectionService(new FixedRunner("\u0001\u0000\u0000\u0000", simulateTimeout: true));

            await Assert.ThrowsAsync<TimeoutException>(() =>
                detectionService.FingerprintAsync(episode, AnalysisMode.Introduction));
        }

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.False(db.DetectionCache.Any(e =>
            e.ItemId == episode.EpisodeId &&
            e.Mode == AnalysisMode.Introduction &&
            e.Type == CacheEntryType.Chromaprint));
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
        var compressed = CreateCacheService().CompressBrotli(fingerprint);

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
        using (new CachingPluginScope(cacheDir, cacheDbPath))
        {
            // Should hit cache because start=0, end=600 matches
            result = await CreateDetectionService().FingerprintAsync(episode, AnalysisMode.Introduction);
        }

        Assert.Equal(fingerprint, result);
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
        var compressed = CreateCacheService().CompressBrotli(fingerprint);

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

        using (new CachingPluginScope(cacheDir, cacheDbPath))
        {
            // Should miss cache (end mismatch: 600 vs 900) and then throw
            // because the file doesn't actually exist for ffmpeg
            await Assert.ThrowsAsync<FFmpegDetectionException>(
                async () => await CreateDetectionService().FingerprintAsync(episode, AnalysisMode.Introduction));
        }
    }

    [Fact]
    public async Task HasCachedFingerprint_ReturnsFalseForStaleEntry()
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
            await db.SaveChangesAsync();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            // Should return false: DB has end=600 but episode expects end=900
            Assert.False(await CreateCacheService().HasCachedFingerprintAsync(episode, AnalysisMode.Introduction));
        }
    }

    [Fact]
    public async Task HasCachedFingerprint_ReturnsTrueForMatchingEntry()
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
            await db.SaveChangesAsync();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(await CreateCacheService().HasCachedFingerprintAsync(episode, AnalysisMode.Introduction));
        }
    }

    [Fact]
    public async Task HasCachedFingerprint_Credits_ReturnsTrueForMatchingEntry()
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
            await db.SaveChangesAsync();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.True(await CreateCacheService().HasCachedFingerprintAsync(episode, AnalysisMode.Credits));
        }
    }

    [Fact]
    public async Task LegacyMigration_Accepted_WhenDurationMatchesCurrentSettings()
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

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            await CreateCacheService().MigrateLegacyCachesAsync([episode]);
        }

        Assert.False(File.Exists(legacyPath), "Legacy file should be deleted after successful migration");

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var entry = db.DetectionCache.FirstOrDefault(e =>
            e.ItemId == episode.EpisodeId &&
            e.Mode == AnalysisMode.Introduction &&
            e.Type == CacheEntryType.Chromaprint);
        Assert.NotNull(entry);
        Assert.Equal(0, entry.Start);
        Assert.Equal(600, entry.End);

        var migrated = ReadFingerprintFromDb(db, episode.EpisodeId, AnalysisMode.Introduction);
        Assert.Equal(4825, migrated.Length);
    }

    [Fact]
    public async Task LegacyMigration_Rejected_WhenDurationMismatch()
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
            await CreateCacheService().MigrateLegacyCachesAsync([episode]);
        }

        Assert.True(File.Exists(legacyPath), "Rejected legacy file should be retained for retry or inspection");

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.False(
            db.DetectionCache.Any(e =>
                e.ItemId == episode.EpisodeId &&
                e.Type == CacheEntryType.Chromaprint),
            "No DB row should be written for rejected legacy file");
    }

    [Fact]
    public async Task LegacyMigration_Credits_AcceptedWithCorrectStartEnd()
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

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            await CreateCacheService().MigrateLegacyCachesAsync([episode]);
        }

        Assert.False(File.Exists(legacyPath));

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var entry = db.DetectionCache.FirstOrDefault(e =>
            e.ItemId == episode.EpisodeId &&
            e.Mode == AnalysisMode.Credits &&
            e.Type == CacheEntryType.Chromaprint);
        Assert.NotNull(entry);
        Assert.Equal(1560, entry.Start);
        Assert.Equal(1800, entry.End);

        var migrated = ReadFingerprintFromDb(db, episode.EpisodeId, AnalysisMode.Credits);
        Assert.Equal(lineCount, migrated.Length);
    }

    [Fact]
    public async Task MigrateLegacyCachesAsync_CacheDisabledThenEnabled_MigratesOnRetry()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 600,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, episode.EpisodeId.ToString("N"));

        await File.WriteAllLinesAsync(
            legacyPath,
            Enumerable.Range(0, 4825).Select(i => ((uint)(i * 12345)).ToString(CultureInfo.InvariantCulture)));

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            var cacheService = CreateCacheService();
            SetCacheFingerprints(false);

            await cacheService.MigrateLegacyCachesAsync([episode]);

            Assert.True(File.Exists(legacyPath));

            SetCacheFingerprints(true);
            await cacheService.MigrateLegacyCachesAsync([episode]);
        }

        Assert.False(File.Exists(legacyPath));

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.True(db.DetectionCache.Any(e =>
            e.ItemId == episode.EpisodeId &&
            e.Mode == AnalysisMode.Introduction &&
            e.Type == CacheEntryType.Chromaprint));
    }

    [Fact]
    public async Task MigrateLegacyCachesAsync_DbWriteFailure_RetriesOnNextCall()
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/does/not/exist.mkv",
            IntroFingerprintEnd = 600,
        };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, episode.EpisodeId.ToString("N"));

        await File.WriteAllLinesAsync(
            legacyPath,
            Enumerable.Range(0, 4825).Select(i => ((uint)(i * 12345)).ToString(CultureInfo.InvariantCulture)));

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            var cacheService = CreateCacheService();
            var invalidPath = Path.Join(cacheDir, "missing", "introskipper-cache.db");

            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "_cacheDbPath", invalidPath);
            await cacheService.MigrateLegacyCachesAsync([episode]);

            Assert.True(File.Exists(legacyPath));

            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "_cacheDbPath", cacheDbPath);
            await cacheService.MigrateLegacyCachesAsync([episode]);
        }

        Assert.False(File.Exists(legacyPath));

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.True(db.DetectionCache.Any(e =>
            e.ItemId == episode.EpisodeId &&
            e.Mode == AnalysisMode.Introduction &&
            e.Type == CacheEntryType.Chromaprint));
    }

    [Fact]
    public async Task MigrateLegacyCachesAsync_UnsupportedSuffix_IgnoredForMigrationButDeletedAsStale()
    {
        var episodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, $"{episodeId:N}-unsupported-suffix");

        await File.WriteAllTextAsync(legacyPath, "legacy data");

        using (new CachingPluginScope(cacheDir))
        {
            await CreateCacheService().MigrateLegacyCachesAsync([
                new QueuedEpisode { EpisodeId = episodeId, Path = "/does/not/exist.mkv" }]);
        }

        Assert.True(File.Exists(legacyPath));

        using (new CachingPluginScope(cacheDir))
        {
            await CreateCacheService().DeleteStaleCachesAsync(new HashSet<Guid>());
        }

        Assert.False(File.Exists(legacyPath));
    }

    [Fact]
    public async Task MigrateLegacyCachesAsync_SuccessfulMigration_IsIdempotentOnRepeatCall()
    {
        var episodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, $"{episodeId:N}-silence-10.5-20.5-v2");

        await File.WriteAllTextAsync(legacyPath, "silence_start: 1.0\nsilence_end: 2.5\n");

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            var cacheService = CreateCacheService();
            var episode = new QueuedEpisode { EpisodeId = episodeId, Path = "/does/not/exist.mkv" };

            await cacheService.MigrateLegacyCachesAsync([episode]);
            await cacheService.MigrateLegacyCachesAsync([episode]);
        }

        Assert.False(File.Exists(legacyPath));

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.Equal(
            Enum.GetValues<AnalysisMode>().Length,
            db.DetectionCache.Count(e => e.ItemId == episodeId && e.Type == CacheEntryType.Silence));
    }

    [Theory]
    [InlineData("blackframes-10.5-20.5-v1", 10.5, 20.5)]
    [InlineData("blackframes-10.5-alt", 10.5, 0)]
    public async Task MigrateLegacyCachesAsync_BlackframeFile_WritesCreditsMode(string suffix, double expectedStart, double expectedEnd)
    {
        var episodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, $"{episodeId:N}-{suffix}");

        await File.WriteAllTextAsync(
            legacyPath,
            "[Parsed_blackframe_0 @ 0x0000000] frame:1 pblack:99 pts:43 t:0.043000 type:B last_keyframe:0");

        var episode = new QueuedEpisode
        {
            EpisodeId = episodeId,
            Path = "/does/not/exist.mkv",
        };

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            SetCacheConfiguration(new PluginConfiguration { CacheFingerprints = true, BlackFrameThreshold = 32 });
            await CreateCacheService().MigrateLegacyCachesAsync([episode]);
        }

        Assert.False(File.Exists(legacyPath));

        using var db = new DetectionCacheDbContext(cacheDbPath);
        Assert.Equal(1, db.DetectionCache.Count(e => e.ItemId == episodeId && e.Type == CacheEntryType.BlackFrame));
        Assert.True(db.DetectionCache.Any(e =>
            e.ItemId == episodeId &&
            e.Type == CacheEntryType.BlackFrame &&
            e.Variant == (Math.Abs(expectedEnd) < 1e-9 ? DetectionCacheVariant.BlackFrameCredits(32) : DetectionCacheVariant.BlackFrameRange(32)) &&
            e.Mode == AnalysisMode.Credits));
        Assert.False(db.DetectionCache.Any(e =>
            e.ItemId == episodeId &&
            e.Type == CacheEntryType.BlackFrame &&
            e.Mode == AnalysisMode.Introduction));

        var blackFrames = ReadDetectionCache<BlackFrame>(
            db,
            episodeId,
            AnalysisMode.Credits,
            CacheEntryType.BlackFrame,
            expectedStart,
            expectedEnd);
        var blackFrame = Assert.Single(blackFrames);
        Assert.Equal(99, blackFrame.Percentage);
        Assert.Equal(expectedStart + 0.043, blackFrame.Time, 3);
        Assert.Equal(1, blackFrame.Frame);
    }

    [Theory]
    [InlineData("blackframes-10.5-20.5-v1", 10.5, 20.5)]
    [InlineData("blackframes-10.5-alt", 10.5, 0)]
    public async Task MigrateLegacyCachesAsync_BlackframeFile_UsesDefaultVariant(string suffix, double expectedStart, double expectedEnd)
    {
        var episodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, $"{episodeId:N}-{suffix}");

        await File.WriteAllTextAsync(
            legacyPath,
            "[Parsed_blackframe_0 @ 0x0000000] frame:1 pblack:99 pts:43 t:0.043000 type:B last_keyframe:0");

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            await CreateCacheService().MigrateLegacyCachesAsync([
                new QueuedEpisode { EpisodeId = episodeId, Path = "/does/not/exist.mkv" }]);
        }

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var entry = Assert.Single(db.DetectionCache.Where(e => e.ItemId == episodeId && e.Type == CacheEntryType.BlackFrame));
        Assert.Equal(
            Math.Abs(expectedEnd) < 1e-9 ? DetectionCacheVariant.BlackFrameCredits(28) : DetectionCacheVariant.BlackFrameRange(28),
            entry.Variant);
        Assert.Equal(expectedStart, entry.Start);
        Assert.Equal(expectedEnd, entry.End);
    }

    [Fact]
    public async Task MigrateLegacyCachesAsync_ModeAgnosticSilenceFile_WritesAllModes()
    {
        var episodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, $"{episodeId:N}-silence-10.5-20.5-v2");

        await File.WriteAllTextAsync(legacyPath, "silence_start: 1.0\nsilence_end: 2.5\n");

        var episode = new QueuedEpisode
        {
            EpisodeId = episodeId,
            Path = "/does/not/exist.mkv",
        };

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            SetCacheConfiguration(new PluginConfiguration { CacheFingerprints = true, SilenceDetectionMaximumNoise = -45 });
            var cacheService = CreateCacheService();

            await cacheService.MigrateLegacyCachesAsync([episode]);
        }

        Assert.False(File.Exists(legacyPath));

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var modes = Enum.GetValues<AnalysisMode>();
        Assert.Equal(
            modes.Length,
            db.DetectionCache.Count(e => e.ItemId == episodeId && e.Type == CacheEntryType.Silence));
        Assert.All(
            db.DetectionCache.Where(e => e.ItemId == episodeId && e.Type == CacheEntryType.Silence),
            entry => Assert.Equal(DetectionCacheVariant.Silence(-45), entry.Variant));

        foreach (var silenceRange in modes.Select(mode => Assert.Single(ReadDetectionCache<TimeRange>(
            db,
            episodeId,
            mode,
            CacheEntryType.Silence,
            10.5,
            20.5))))
        {
            Assert.Equal(11.5, silenceRange.Start);
            Assert.Equal(13.0, silenceRange.End);
        }
    }

    [Fact]
    public async Task MigrateLegacyCachesAsync_SilenceFile_UsesDefaultVariant()
    {
        var episodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, $"{episodeId:N}-silence-10.5-20.5-v2");

        await File.WriteAllTextAsync(legacyPath, "silence_start: 1.0\nsilence_end: 2.5\n");

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            await CreateCacheService().MigrateLegacyCachesAsync([
                new QueuedEpisode { EpisodeId = episodeId, Path = "/does/not/exist.mkv" }]);
        }

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var silenceEntries = db.DetectionCache.Where(e => e.ItemId == episodeId && e.Type == CacheEntryType.Silence).ToList();
        Assert.Equal(Enum.GetValues<AnalysisMode>().Length, silenceEntries.Count);
        Assert.All(
            silenceEntries,
            entry => Assert.Equal(DetectionCacheVariant.Silence(-50), entry.Variant));
    }

    [Fact]
    public async Task MigrateLegacyCachesAsync_SilenceVariant_ChangesWithCurrentNoiseSetting()
    {
        var defaultEpisodeId = Guid.NewGuid();
        var configuredEpisodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var defaultLegacyPath = Path.Join(cacheDir, $"{defaultEpisodeId:N}-silence-10.5-20.5-v2");
        var configuredLegacyPath = Path.Join(cacheDir, $"{configuredEpisodeId:N}-silence-10.5-20.5-v2");

        await File.WriteAllTextAsync(defaultLegacyPath, "silence_start: 1.0\nsilence_end: 2.5\n");

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            await CreateCacheService().MigrateLegacyCachesAsync([
                new QueuedEpisode { EpisodeId = defaultEpisodeId, Path = "/does/not/exist.mkv" }]);

            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(configuredLegacyPath, "silence_start: 1.0\nsilence_end: 2.5\n");
            SetCacheConfiguration(new PluginConfiguration { CacheFingerprints = true, SilenceDetectionMaximumNoise = -45 });
            await CreateCacheService().MigrateLegacyCachesAsync([
                new QueuedEpisode { EpisodeId = configuredEpisodeId, Path = "/does/not/exist.mkv" }]);
        }

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var defaultEntries = db.DetectionCache.Where(e => e.ItemId == defaultEpisodeId && e.Type == CacheEntryType.Silence).ToList();
        var configuredEntries = db.DetectionCache.Where(e => e.ItemId == configuredEpisodeId && e.Type == CacheEntryType.Silence).ToList();
        Assert.Equal(Enum.GetValues<AnalysisMode>().Length, defaultEntries.Count);
        Assert.Equal(Enum.GetValues<AnalysisMode>().Length, configuredEntries.Count);
        Assert.All(
            defaultEntries,
            entry => Assert.Equal(DetectionCacheVariant.Silence(-50), entry.Variant));
        Assert.All(
            configuredEntries,
            entry => Assert.Equal(DetectionCacheVariant.Silence(-45), entry.Variant));
    }

    [Fact]
    public async Task MigrateLegacyCachesAsync_ModeAgnosticKeyframeFile_WritesAllModesWithFinalVariant()
    {
        var episodeId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var legacyPath = Path.Join(cacheDir, $"{episodeId:N}-keyframes-10.5-20.5-v1");

        await File.WriteAllTextAsync(legacyPath, "[Parsed_showinfo_0] n:0 pts_time:0.5\n[Parsed_showinfo_0] n:1 pts_time:2.0\n");

        string cacheDbPath;
        using (var scope = new CachingPluginScope(cacheDir))
        {
            cacheDbPath = scope.CacheDbPath;
            await CreateCacheService().MigrateLegacyCachesAsync([
                new QueuedEpisode { EpisodeId = episodeId, Path = "/does/not/exist.mkv" }]);
        }

        Assert.False(File.Exists(legacyPath));

        using var db = new DetectionCacheDbContext(cacheDbPath);
        var modes = Enum.GetValues<AnalysisMode>();
        Assert.Equal(
            modes.Length,
            db.DetectionCache.Count(e => e.ItemId == episodeId && e.Type == CacheEntryType.Keyframe));
        Assert.All(
            db.DetectionCache.Where(e => e.ItemId == episodeId && e.Type == CacheEntryType.Keyframe),
            entry => Assert.Equal(DetectionCacheVariant.Keyframe(), entry.Variant));
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
        var compressed = CreateCacheService().CompressBrotli(original);
        var decompressed = CreateCacheService().DecompressBrotli<uint[]>(compressed);

        Assert.NotNull(decompressed);
        Assert.Equal(original, decompressed);
    }

    private static DetectionCacheService CreateCacheService() => TestServiceFactory.CreateCacheService();

    private static IMediaDetectionService CreateDetectionService() => TestServiceFactory.CreateDetectionService();

    private static IMediaDetectionService CreateDetectionService(IFFmpegRunner runner)
    {
        var optionsProvider = new PluginOptionsProvider();
        return new MediaDetectionService(
            runner,
            new DetectionCacheService(optionsProvider, NullLogger<DetectionCacheService>.Instance),
            optionsProvider,
            NullLogger<MediaDetectionService>.Instance);
    }

    private static void SetCacheFingerprints(bool enabled)
    {
        var plugin = Plugin.Instance;
        Assert.NotNull(plugin);

        EntrypointTestHelpers.SetPropertyOrField(
            plugin,
            "Configuration",
            new PluginConfiguration { CacheFingerprints = enabled });
    }

    private static void SetCacheConfiguration(PluginConfiguration configuration)
    {
        var plugin = Plugin.Instance;
        Assert.NotNull(plugin);

        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", configuration);
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

        var result = CreateCacheService().DecompressBrotli<uint[]>(entry.Data);
        return result ?? [];
    }

    private static T[] ReadDetectionCache<T>(
        DetectionCacheDbContext db,
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end)
    {
        var entry = db.DetectionCache.FirstOrDefault(e =>
            e.ItemId == itemId &&
            e.Mode == mode &&
            e.Type == type &&
            e.Start == start &&
            e.End == end);

        if (entry is null)
        {
            return [];
        }

        var result = CreateCacheService().DecompressBrotli<T[]>(entry.Data);
        return result ?? [];
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

    private sealed class FixedRunner(string output, bool simulateTimeout = false, int exitCode = 0) : IFFmpegRunner
    {
        public Task<FFmpegProcessResult> RunAsync(
            IReadOnlyList<string> args,
            FFmpegOutputStream outputStream = FFmpegOutputStream.Stdout,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateResult());

        private FFmpegProcessResult CreateResult()
            => simulateTimeout
                ? new(System.Text.Encoding.UTF8.GetBytes(output), Array.Empty<byte>(), FFmpegProcessStatus.TimedOut, null)
                : new(System.Text.Encoding.UTF8.GetBytes(output), Array.Empty<byte>(), FFmpegProcessStatus.Completed, exitCode);
    }

    [Fact]
    public async Task HasCachedFingerprint_ReturnsFalseForNonChromaprintVariant()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);

        using (var db = Plugin.CreateCacheDbContext())
        {
            db.DetectionCache.Add(new DbDetectionCache(
                episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, "chromaprint:v0", EntrypointTestHelpers.EmptyJsonArray));
            await db.SaveChangesAsync();
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            Assert.False(await CreateCacheService().HasCachedFingerprintAsync(episode, AnalysisMode.Introduction));
        }
    }
}
