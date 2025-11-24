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

    [Fact]
    public void SanitizeBlackFrameOutput_FiltersNonMatchingLines()
    {
        var raw = "Input #0, matroska\n" +
                  "[Parsed_blackframe_0 @ 0x1] frame:0 pblack:69 pts:0 t:0.000000 type:I last_keyframe:0\n" +
                  "Stream mapping:\n" +
                  "[Parsed_blackframe_0 @ 0x1] frame:1 pblack:77 pts:40 t:0.040000 type:B last_keyframe:0\n";

        var sanitized = FFmpegWrapper.SanitizeBlackFrameOutput(raw);

        Assert.NotEqual(raw, sanitized);
        Assert.Contains("frame:0", sanitized, StringComparison.Ordinal);
        Assert.Contains("frame:1", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Input #0", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Stream mapping", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeSilenceOutput_FiltersNonMatchingLines()
    {
        var raw = "Input #0, audio\n" +
                  "[silencedetect @ 0x1] silence_start: 12.34\n" +
                  "Random noise\n" +
                  "[silencedetect @ 0x1] silence_end: 56.789 | silence_duration: 44.449\n";

        var sanitized = FFmpegWrapper.SanitizeSilenceOutput(raw);

        Assert.NotEqual(raw, sanitized);
        Assert.Contains("silence_start", sanitized, StringComparison.Ordinal);
        Assert.Contains("silence_end", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Input #0", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Random noise", sanitized, StringComparison.Ordinal);
    }
}
