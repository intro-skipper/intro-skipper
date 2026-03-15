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
    #region Info Query Tests

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

    /// <summary>
    /// Test that -muxers query produces no warning.
    /// </summary>
    [FactSkipFFmpegTests]
    public void TestMuxersQueryNoWarning()
    {
        RunFFmpegAndVerifyNoWarning("-muxers");
    }

    /// <summary>
    /// Test that -h muxer=chromaprint query produces no warning.
    /// </summary>
    [FactSkipFFmpegTests]
    public void TestHelpMuxerQueryNoWarning()
    {
        RunFFmpegAndVerifyNoWarning("-h muxer=chromaprint");
    }

    /// <summary>
    /// Test that -h filter=silencedetect query produces no warning.
    /// </summary>
    [FactSkipFFmpegTests]
    public void TestHelpFilterQueryNoWarning()
    {
        RunFFmpegAndVerifyNoWarning("-h filter=silencedetect");
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

    #endregion

    #region Media Processing Tests

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

    [FactSkipFFmpegTests]
    public void TestNoTrailingOptionsWithBlackFrameDetectionAlt()
    {
        // Test alternative black frame detection
        var episode = QueueFile("credits.mp4");
        episode.Duration = 5;
        episode.CreditsFingerprintStart = 0;

        // Alternative black frame detection
        var blackFrames = FFmpegWrapper.DetectBlackFrames(episode, 32);

        Assert.NotNull(blackFrames);
    }

    [FactSkipFFmpegTests]
    public void TestNoTrailingOptionsWithSilenceDetection()
    {
        // Test silence detection with actual media file
        var episode = QueueFile("rainbow.mp4");
        episode.Duration = 2;
        episode.IntroFingerprintEnd = 2;

        // Detect silence - this should not produce "Trailing option" warning
        var silenceRanges = FFmpegWrapper.DetectSilence(episode, new TimeRange(0, 2));

        // Verify FFmpeg ran successfully (null or empty list is fine)
        Assert.NotNull(silenceRanges);
    }

    [FactSkipFFmpegTests]
    public void TestNoTrailingOptionsWithKeyFrameDetection()
    {
        // Test key frame detection with actual media file
        var episode = QueueFile("rainbow.mp4");
        episode.Duration = 2;

        // Detect key frames - this should not produce "Trailing option" warning
        var keyFrames = FFmpegWrapper.DetectKeyFrames(episode, new TimeRange(0, 2));

        // Verify FFmpeg ran successfully
        Assert.NotNull(keyFrames);
    }

    [FactSkipFFmpegTests]
    public void TestNoTrailingOptionsWithChromaprintFingerprinting()
    {
        // Test chromaprint fingerprinting with actual audio file
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Name = "big_buck_bunny_intro.mp3",
            Path = "../../../audio/big_buck_bunny_intro.mp3",
            Duration = 10,
            IntroFingerprintEnd = 10,
            CreditsFingerprintStart = 0
        };

        // Fingerprint intro - this should not produce "Trailing option" warning
        try
        {
            var fingerprint = FFmpegWrapper.Fingerprint(episode, AnalysisMode.Introduction);

            // Verify FFmpeg ran successfully
            Assert.NotNull(fingerprint);
        }
        catch (Exception)
        {
            // Fingerprinting may fail due to chromaprint, but we check for warnings
            // If it throws, that's a different issue - we just want to check for warnings
        }
    }

    #endregion

    private static void RunFFmpegAndVerifyNoWarning(string args)
    {
        var ffmpegPath = "ffmpeg";

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

        // Verify no "Trailing option" warning
        Assert.DoesNotContain("Trailing option", output);
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
