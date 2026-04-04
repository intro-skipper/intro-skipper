// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Globalization;
using System.IO;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Xunit;

public sealed class TestCacheOperations
{
    [Fact]
    public void DeleteCacheFiles_Introduction_DeletesIntroFilesOnly()
    {
        var id = Guid.NewGuid().ToString("N");
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var shouldKeep = new[]
        {
            id + "-credits-chromaprint-v1",
            id + "-credits-blackframes-100.5-v2",
        };
        var shouldDelete = new[]
        {
            id + "-chromaprint-v1",
            id + "-silence-0-30-v3",
            id + "-keyframes-0-30-v2",
            id + "-blackframes-0-30-v2",
        };

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);
        using (var db = Plugin.CreateCacheDb())
        {
            foreach (var key in shouldKeep)
            {
                db.Write(key, [0x01]);
            }

            foreach (var key in shouldDelete)
            {
                db.Write(key, [0x01]);
            }
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            FFmpegWrapper.DeleteCacheFiles(AnalysisMode.Introduction);
        }

        using (var db = Plugin.CreateCacheDb())
        {
            foreach (var key in shouldDelete)
            {
                Assert.False(db.ExistsByKey(key), $"DB row '{key}' should be deleted");
            }

            foreach (var key in shouldKeep)
            {
                Assert.True(db.ExistsByKey(key), $"DB row '{key}' should be kept");
            }
        }
    }

    [Fact]
    public void DeleteCacheFiles_Credits_DeletesCreditsFilesOnly()
    {
        var id = Guid.NewGuid().ToString("N");
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var shouldKeep = new[]
        {
            id + "-chromaprint-v1",
            id + "-silence-0-30-v3",
            id + "-keyframes-0-30-v2",
            id + "-blackframes-0-30-v2",
        };
        var shouldDelete = new[]
        {
            id + "-credits-chromaprint-v1",
            id + "-credits-blackframes-100.5-v2",
        };

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);
        using (var db = Plugin.CreateCacheDb())
        {
            foreach (var key in shouldKeep)
            {
                db.Write(key, [0x01]);
            }

            foreach (var key in shouldDelete)
            {
                db.Write(key, [0x01]);
            }
        }

        using (new CachingPluginScope(cacheDir, scope.CacheDbPath))
        {
            FFmpegWrapper.DeleteCacheFiles(AnalysisMode.Credits);
        }

        using (var db = Plugin.CreateCacheDb())
        {
            foreach (var key in shouldDelete)
            {
                Assert.False(db.ExistsByKey(key), $"DB row '{key}' should be deleted");
            }

            foreach (var key in shouldKeep)
            {
                Assert.True(db.ExistsByKey(key), $"DB row '{key}' should be kept");
            }
        }
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsTrueForDbRow()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);
        using (var db = Plugin.CreateCacheDb())
        {
            db.Write(episode.EpisodeId.ToString("N") + "-chromaprint-v1", [0x01]);
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
        var dbKey = id + "-chromaprint-v1";

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

        // Use DetectionCacheDb directly since Plugin.Instance is no longer set after scope disposal.
        using var db = new IntroSkipper.Db.DetectionCacheDb(cacheDbPath);
        Assert.True(db.ExistsByKey(dbKey), "DB row should be created after migration");

        // Verify the fingerprint was correctly round-tripped.
        var migrated = ReadFingerprintFromDb(db, dbKey);
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
        var dbKey = id + "-chromaprint-v1";

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

        using var db = new IntroSkipper.Db.DetectionCacheDb(cacheDbPath);
        Assert.False(db.ExistsByKey(dbKey), "No DB row should be written for a corrupt legacy file");
    }

    private static uint[] ReadFingerprintFromDb(IntroSkipper.Db.DetectionCacheDb db, string cacheKey)
    {
        if (!db.TryRead(cacheKey, out var data))
        {
            return [];
        }

        using var ms = new MemoryStream(data);
        using var reader = new System.IO.BinaryReader(ms);
        var count = reader.ReadInt32();
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = reader.ReadUInt32();
        }

        return result;
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
