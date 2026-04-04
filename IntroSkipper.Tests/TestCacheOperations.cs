// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
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
            Path.Combine(cacheDir, $"{id}-credits-chromaprint-v1"),
            Path.Combine(cacheDir, $"{id}-credits-blackframes-100.5-v2"),
        };
        var shouldDelete = new[]
        {
            Path.Combine(cacheDir, $"{id}-chromaprint-v1"),
            Path.Combine(cacheDir, $"{id}-silence-0-30-v3"),
            Path.Combine(cacheDir, $"{id}-keyframes-0-30-v2"),
            Path.Combine(cacheDir, $"{id}-blackframes-0-30-v2"),
        };

        foreach (var f in shouldKeep.Concat(shouldDelete))
        {
            File.WriteAllText(f, "x");
        }

        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            FFmpegWrapper.DeleteCacheFiles(AnalysisMode.Introduction);
        }

        foreach (var f in shouldDelete)
        {
            Assert.False(File.Exists(f), $"{Path.GetFileName(f)} should be deleted");
        }

        foreach (var f in shouldKeep)
        {
            Assert.True(File.Exists(f), $"{Path.GetFileName(f)} should be kept");
        }
    }

    [Fact]
    public void DeleteCacheFiles_Credits_DeletesCreditsFilesOnly()
    {
        var id = Guid.NewGuid().ToString("N");
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var shouldKeep = new[]
        {
            Path.Combine(cacheDir, $"{id}-chromaprint-v1"),
            Path.Combine(cacheDir, $"{id}-silence-0-30-v3"),
            Path.Combine(cacheDir, $"{id}-keyframes-0-30-v2"),
            Path.Combine(cacheDir, $"{id}-blackframes-0-30-v2"),
        };
        var shouldDelete = new[]
        {
            Path.Combine(cacheDir, $"{id}-credits-chromaprint-v1"),
            Path.Combine(cacheDir, $"{id}-credits-blackframes-100.5-v2"),
        };

        foreach (var f in shouldKeep.Concat(shouldDelete))
        {
            File.WriteAllText(f, "x");
        }

        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            FFmpegWrapper.DeleteCacheFiles(AnalysisMode.Credits);
        }

        foreach (var f in shouldDelete)
        {
            Assert.False(File.Exists(f), $"{Path.GetFileName(f)} should be deleted");
        }

        foreach (var f in shouldKeep)
        {
            Assert.True(File.Exists(f), $"{Path.GetFileName(f)} should be kept");
        }
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsTrueForBinaryFile()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        File.WriteAllText(Path.Combine(cacheDir, episode.EpisodeId.ToString("N") + "-chromaprint-v1"), "x");

        using var _ = new CachingPluginScope(cacheDir);
        Assert.True(FFmpegWrapper.HasCachedFingerprint(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public void HasCachedFingerprint_ReturnsTrueForLegacyFile()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        // Legacy path: just the episode ID, no suffix
        File.WriteAllText(Path.Combine(cacheDir, episode.EpisodeId.ToString("N")), "x");

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
    public void LegacyFingerprintCache_MigratedToBinaryAndDeleted()
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
        var binaryPath = Path.Combine(cacheDir, id + "-chromaprint-v1");

        // Write a valid legacy text-format fingerprint (one uint per line)
        uint[] expectedFingerprint = [1234567890u, 987654321u, 42u];
        File.WriteAllLines(
            legacyPath,
            Array.ConvertAll(expectedFingerprint, v => v.ToString(CultureInfo.InvariantCulture)));

        uint[] result;
        using (new CachingPluginScope(cacheDir))
        {
            result = FFmpegWrapper.Fingerprint(episode, AnalysisMode.Introduction);
        }

        Assert.False(File.Exists(legacyPath), "Legacy text cache file should be deleted after migration");
        Assert.True(File.Exists(binaryPath), "Binary cache file should be created");
        Assert.Equal(expectedFingerprint, result);
    }

    /// <summary>
    /// Sets up Plugin.Instance with a real cache dir and CacheFingerprints enabled.
    /// </summary>
    private sealed class CachingPluginScope : IDisposable
    {
        private readonly EntrypointTestHelpers.PluginInstanceScope _inner;

        public CachingPluginScope(string cacheDir)
        {
            _inner = new EntrypointTestHelpers.PluginInstanceScope(cacheDir);
            var plugin = Plugin.Instance;
            if (plugin is not null)
            {
                EntrypointTestHelpers.SetPropertyOrField(
                    plugin,
                    "Configuration",
                    new PluginConfiguration { CacheFingerprints = true });
            }
        }

        public void Dispose() => _inner.Dispose();
    }
}
