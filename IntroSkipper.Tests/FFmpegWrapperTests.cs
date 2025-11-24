// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.IO;
using IntroSkipper.Data;
using Xunit;

namespace IntroSkipper.Tests;

public class FFmpegWrapperTests
{
    [Fact]
    public void CacheFingerprint_WritesBinaryBlobReadableByReader()
    {
        // Arrange
        var testDirectory = Path.Combine(Path.GetTempPath(), "IntroSkipperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
            var points = new List<uint> { 1u, 5u, 42u, 0xFFFFFFF0u };

            // Act
            FFmpegWrapper.CacheFingerprint(episode, AnalysisMode.Introduction, points, testDirectory);

            // Assert
            var cachePath = FFmpegWrapper.GetFingerprintCachePath(episode, AnalysisMode.Introduction, testDirectory);
            Assert.True(File.Exists(cachePath));
            Assert.False(File.Exists(cachePath + ".tmp"));

            using var stream = File.OpenRead(cachePath);
            Assert.True(FFmpegWrapper.TryReadBinaryFingerprint(stream, out var restored));
            Assert.Equal(points, restored);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void BlackFrameCache_RoundTripsBinaryStructure()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "IntroSkipperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var cacheKey = "blackframe-test";
            var frames = new List<BlackFrame>
            {
                new(96, 0.0, 0),
                new(88, 0.5, 12),
                new(100, 1.0, 24)
            };

            FFmpegWrapper.StoreBlackFrameCache(cacheKey, frames, testDirectory);

            Assert.True(FFmpegWrapper.TryLoadBlackFrameCache(cacheKey, out var restored, testDirectory));
            Assert.Equal(frames, restored);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, true);
            }
        }
    }

    [Fact]
    public void SilenceCache_RoundTripsBinaryStructure()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "IntroSkipperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        try
        {
            var cacheKey = "silence-test";
            var ranges = new List<TimeRange>
            {
                new(12.34, 20.00),
                new(46.5, 55.5)
            };

            FFmpegWrapper.StoreSilenceCache(cacheKey, ranges, testDirectory);

            Assert.True(FFmpegWrapper.TryLoadSilenceCache(cacheKey, out var restored, testDirectory));
            Assert.Equal(ranges.Count, restored.Length);
            for (var i = 0; i < ranges.Count; i++)
            {
                Assert.Equal(ranges[i].Start, restored[i].Start, 3);
                Assert.Equal(ranges[i].End, restored[i].End, 3);
            }
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, true);
            }
        }
    }
}
