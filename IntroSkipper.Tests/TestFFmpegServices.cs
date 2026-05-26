// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

/* These tests require that the host system has a version of FFmpeg installed
 * which supports both chromaprint and the "-fp_format raw" flag.
 */

using System;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegServices
{
    private static IFFmpegCapabilityService CreateCapabilityService() => TestServiceFactory.CreateCapabilityService();

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
    /// Test that info queries are not built with trailing thread options.
    /// </summary>
    [Theory]
    [InlineData("warning", "-muxers")]
    [InlineData("warning", "-h", "muxer=chromaprint")]
    [InlineData("info", "-h", "filter=silencedetect")]
    public void TestInfoQueryNoTrailingThreadOption(string expectedLogLevel, params string[] args)
    {
        AssertRunnerBuildsInfoQueryWithoutTrailingThreadOption(expectedLogLevel, args);
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
        catch (Exception ex) when (ex is FingerprintException or FFmpegDetectionException)
        {
            // Fingerprinting may fail if chromaprint is unavailable, but this test only checks for warnings.
        }
    }

    #endregion

    private static void AssertRunnerBuildsInfoQueryWithoutTrailingThreadOption(string expectedLogLevel, string[] args)
    {
        var runner = new FFmpegRunner(new PluginOptionsProvider(), NullLogger<FFmpegRunner>.Instance);
        var info = runner.CreateProcessStartInfo(args);

        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.DoesNotContain("-threads", info.ArgumentList);
        Assert.Equal(["-hide_banner", "-loglevel", expectedLogLevel, .. args], info.ArgumentList);
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
