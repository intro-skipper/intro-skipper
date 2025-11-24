// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.IO;
using IntroSkipper;
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
}
