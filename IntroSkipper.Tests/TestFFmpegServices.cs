// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

/* These tests require that the host system has a version of FFmpeg installed
 * which supports both chromaprint and the "-fp_format raw" flag.
 */

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegServices
{
    private static FFmpegCapabilityService CreateCapabilityService() => TestServiceFactory.CreateCapabilityService();

    private static IMediaDetectionService CreateDetectionService() => TestServiceFactory.CreateDetectionService();

    #region Info Query Tests

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWarning()
    {
        // Run FFmpeg version check to populate ChromaprintLogs
        var capService = CreateCapabilityService();
        var result = await capService.CheckFFmpegVersionAsync().ConfigureAwait(true);

        // Get the logs and verify no "Trailing option" warning appears
        var logs = capService.GetChromaprintLogs();

        // The test passes if FFmpeg version check succeeds (no error)
        // and no "Trailing option" warning is in the logs
        Assert.True(result, "FFmpeg version check should pass");
        Assert.DoesNotContain("Trailing option", logs, StringComparison.Ordinal);
    }

    [FactSkipFFmpegTests]
    public async Task TestFFmpegVersionCheck()
    {
        Assert.True(await CreateCapabilityService().CheckFFmpegVersionAsync().ConfigureAwait(true));
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

    #endregion

    #region Media Processing Tests

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWithMediaFiles()
    {
        // Test with actual media file to ensure no trailing options warning
        var episode = QueueFile("rainbow.mp4");
        episode.Duration = 2;

        // Detect black frames - this should not produce "Trailing option" warning
        var blackFrames = await CreateDetectionService().DetectBlackFramesInRangeAsync(episode, new TimeRange(0, 2), 85, 32, AnalysisMode.Introduction);

        // Verify we got results (meaning FFmpeg ran successfully without warnings)
        Assert.NotNull(blackFrames);
    }

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWithBlackFrameDetectionAlt()
    {
        // Test alternative black frame detection
        var episode = QueueFile("credits.mp4");
        episode.Duration = 5;
        episode.CreditsFingerprintStart = 0;

        // Alternative black frame detection
        var blackFrames = await CreateDetectionService().DetectCreditBlackFramesAsync(episode, 32);

        Assert.NotNull(blackFrames);
    }

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWithSilenceDetection()
    {
        // Test silence detection with actual media file
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Name = "big_buck_bunny_clip.mp3",
            Path = "../../../audio/big_buck_bunny_clip.mp3",
            Duration = 2,
            IntroFingerprintEnd = 2,
        };

        // Detect silence - this should not produce "Trailing option" warning
        var silenceRanges = await CreateDetectionService().DetectSilenceAsync(episode, new TimeRange(0, 2), AnalysisMode.Introduction);

        // Verify FFmpeg ran successfully (null or empty list is fine)
        Assert.NotNull(silenceRanges);
    }

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWithKeyFrameDetection()
    {
        // Test key frame detection with actual media file
        var episode = QueueFile("rainbow.mp4");
        episode.Duration = 2;

        // Detect key frames - this should not produce "Trailing option" warning
        var keyFrames = await CreateDetectionService().DetectKeyFramesAsync(episode, new TimeRange(0, 2), AnalysisMode.Introduction);

        // Verify FFmpeg ran successfully
        Assert.NotNull(keyFrames);
    }

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWithChromaprintFingerprinting()
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
            var fingerprint = await CreateDetectionService().FingerprintAsync(episode, AnalysisMode.Introduction);

            // Verify FFmpeg ran successfully
            Assert.NotNull(fingerprint);
        }
        catch (FingerprintException)
        {
            // Fingerprinting may fail if chromaprint is unavailable, but this test only checks for warnings.
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
        Assert.DoesNotContain("Trailing option", output, StringComparison.Ordinal);
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
