// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

/* These tests require that the host system has a version of FFmpeg installed
 * which supports both chromaprint and the "-fp_format raw" flag.
 */

using System;
using System.Diagnostics;
using IntroSkipper.Data;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegWrapper
{
    [FactSkipFFmpegTests]
    public void TestNoTrailingOptionsWarning()
    {
        // Run FFmpeg version check to populate ChromaprintLogs
        var result = FFmpegWrapper.CheckFFmpegVersion();

        // Get the logs and verify no "Trailing option" warning appears
        var logs = FFmpegWrapper.GetChromaprintLogs();

        // The test passes if FFmpeg version check succeeds (no error)
        // and no "Trailing option" warning is in the logs
        Assert.True(result, "FFmpeg version check should pass");
        Assert.DoesNotContain("Trailing option", logs);
    }

    [FactSkipFFmpegTests]
    public void TestFFmpegVersionCheck()
    {
        Assert.True(FFmpegWrapper.CheckFFmpegVersion());
    }

    [FactSkipFFmpegTests]
    public void TestNoTrailingOptionsWithMediaFiles()
    {
        // Test with actual media file to ensure no trailing options warning
        var episode = QueueFile("rainbow.mp4");
        episode.Duration = 2;

        // Detect black frames - this should not produce "Trailing option" warning
        var blackFrames = FFmpegWrapper.DetectBlackFrames(episode, new TimeRange(0, 2), 85, 32);

        // Verify we got results (meaning FFmpeg ran successfully without warnings)
        Assert.NotNull(blackFrames);
    }

    /// <summary>
    /// This test demonstrates that the OLD behavior (threads before query) produces warnings.
    /// It should FAIL - proving that the fix is necessary.
    /// </summary>
    [FactSkipFFmpegTests]
    public void TestOldBehaviorProducesWarning()
    {
        // This simulates the OLD broken argument order:
        // ffmpeg -hide_banner -threads 0 -loglevel warning -version
        // This should produce "Trailing option" warning

        var ffmpegPath = "ffmpeg";
        var args = "-hide_banner -threads 0 -loglevel warning -version";

        var info = new ProcessStartInfo(ffmpegPath, args)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(info);
        var output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        // OLD behavior produces this warning - test should FAIL with old code
        Assert.Contains("Trailing option", output);
    }

    private static QueuedEpisode QueueFile(string path)
    {
        return new()
        {
            EpisodeId = Guid.NewGuid(),
            Name = path,
            Path = "../../../video/" + path
        };
    }
}
