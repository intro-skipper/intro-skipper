// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IntroSkipper.Data;
using IntroSkipper.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class UpgradeFingerprintCacheTaskTests
{
    [Fact]
    public void MigrateExtensionlessBinaryCaches_RenamesBlackframesAndSilenceAndSkipsOtherFiles()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "IntroSkipperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var episodeId = Guid.NewGuid();
            var validEpisodeIds = new HashSet<Guid> { episodeId };

            // Create a valid blackframe cache, then remove its extension to simulate legacy.
            var blackKey = $"{episodeId:N}-blackframes-0-1-v1";
            FFmpegWrapper.StoreBlackFrameCache(blackKey, new List<BlackFrame> { new(96, 0.0, 0) }, testDirectory);
            var blackWithExt = Path.Combine(testDirectory, blackKey + ".ifbc");
            var blackLegacy = Path.Combine(testDirectory, blackKey);
            Assert.True(File.Exists(blackWithExt));
            File.Move(blackWithExt, blackLegacy, overwrite: true);
            Assert.True(File.Exists(blackLegacy));

            // Create a valid silence cache, then remove its extension to simulate legacy.
            var silenceKey = $"{episodeId:N}-silence-0-1-v2";
            FFmpegWrapper.StoreSilenceCache(silenceKey, new List<TimeRange> { new(0.1, 0.2) }, testDirectory);
            var silenceWithExt = Path.Combine(testDirectory, silenceKey + ".ifsc");
            var silenceLegacy = Path.Combine(testDirectory, silenceKey);
            Assert.True(File.Exists(silenceWithExt));
            File.Move(silenceWithExt, silenceLegacy, overwrite: true);
            Assert.True(File.Exists(silenceLegacy));

            // Create an extensionless, non-cache artifact that should not be renamed.
            var decoyPath = Path.Combine(testDirectory, $"{episodeId:N}-keyframes-0-1-v1");
            File.WriteAllText(decoyPath, "not a binary cache");
            Assert.True(File.Exists(decoyPath));

            // Create a valid blackframe cache for an invalid episode ID; should be ignored by this helper.
            var invalidEpisodeId = Guid.NewGuid();
            var invalidBlackKey = $"{invalidEpisodeId:N}-blackframes-0-1-v1";
            FFmpegWrapper.StoreBlackFrameCache(invalidBlackKey, new List<BlackFrame> { new(88, 1.0, 10) }, testDirectory);
            var invalidBlackWithExt = Path.Combine(testDirectory, invalidBlackKey + ".ifbc");
            var invalidBlackLegacy = Path.Combine(testDirectory, invalidBlackKey);
            File.Move(invalidBlackWithExt, invalidBlackLegacy, overwrite: true);
            Assert.True(File.Exists(invalidBlackLegacy));

            // Act
            var cacheFiles = Directory.EnumerateFiles(testDirectory).ToArray();
            var stats = UpgradeFingerprintCacheTask.MigrateExtensionlessBinaryCaches(
                testDirectory,
                cacheFiles,
                validEpisodeIds,
                NullLogger.Instance,
                CancellationToken.None);

            // Assert
            Assert.Equal(1, stats.MigratedBlackFrames);
            Assert.Equal(1, stats.MigratedSilence);

            Assert.False(File.Exists(blackLegacy));
            Assert.True(File.Exists(blackLegacy + ".ifbc"));

            Assert.False(File.Exists(silenceLegacy));
            Assert.True(File.Exists(silenceLegacy + ".ifsc"));

            Assert.True(File.Exists(decoyPath));
            Assert.True(File.Exists(invalidBlackLegacy));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
