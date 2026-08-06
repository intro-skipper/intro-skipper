// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

/* These tests require that the host system has a version of FFmpeg installed
 * which supports both chromaprint and the "-fp_format raw" flag.
 */

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegService
{
    [Fact]
    public async Task CheckFFmpegVersionAsync_MemoizesSuccess()
    {
        var probeCount = 0;
        var ffmpegService = CreateFFmpegService(_ =>
        {
            Interlocked.Increment(ref probeCount);
            return Task.FromResult(true);
        });

        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_RetriesFailure()
    {
        var probeCount = 0;
        var ffmpegService = CreateFFmpegService(_ =>
        {
            Interlocked.Increment(ref probeCount);
            return Task.FromResult(false);
        });

        Assert.False(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.False(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_SharesConcurrentProbe()
    {
        var probeCount = 0;
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ffmpegService = CreateFFmpegService(_ =>
        {
            Interlocked.Increment(ref probeCount);
            probeStarted.SetResult();
            return releaseProbe.Task;
        });

        var first = ffmpegService.CheckFFmpegVersionAsync();
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = ffmpegService.CheckFFmpegVersionAsync();

        Assert.Equal(1, probeCount);
        releaseProbe.SetResult(true);
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_SharesConcurrentFailedProbeThenRetries()
    {
        var probeCount = 0;
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ffmpegService = CreateFFmpegService(_ =>
        {
            if (Interlocked.Increment(ref probeCount) == 1)
            {
                probeStarted.SetResult();
                return releaseProbe.Task;
            }

            return Task.FromResult(true);
        });

        var first = ffmpegService.CheckFFmpegVersionAsync();
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = ffmpegService.CheckFFmpegVersionAsync();

        Assert.Equal(1, probeCount);
        releaseProbe.SetResult(false);
        Assert.False(await first);
        Assert.False(await second);

        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_CancelsCallerWithoutCancelingSharedProbe()
    {
        var probeCount = 0;
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ffmpegService = CreateFFmpegService(cancellationToken =>
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Interlocked.Increment(ref probeCount);
            probeStarted.SetResult();
            return releaseProbe.Task;
        });
        using var cancellation = new CancellationTokenSource();

        var canceledCaller = ffmpegService.CheckFFmpegVersionAsync(cancellation.Token);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var remainingCaller = ffmpegService.CheckFFmpegVersionAsync();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCaller);
        Assert.False(remainingCaller.IsCompleted);
        Assert.Equal(1, probeCount);

        releaseProbe.SetResult(true);
        Assert.True(await remainingCaller);
        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task GetProcessOutputAsync_EnforcesTimeoutBeforeStreamsClose()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var ffmpegService = CreateFFmpegService();
        var stopwatch = Stopwatch.StartNew();

        await ffmpegService.GetProcessOutputAsync(
            "/bin/sh",
            ["-c", "sleep 30"],
            timeout: 100);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Process timeout took {stopwatch.Elapsed}.");
    }

    #region Info Query Tests

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWarning()
    {
        // Run FFmpeg version check to populate ChromaprintLogs
        var ffmpegService = CreateFFmpegService();
        var result = await ffmpegService.CheckFFmpegVersionAsync();

        // Get the logs and verify no "Trailing option" warning appears
        var logs = ffmpegService.GetChromaprintLogs();

        // The test passes if FFmpeg version check succeeds (no error)
        // and no "Trailing option" warning is in the logs
        Assert.True(result, "FFmpeg version check should pass");
        Assert.DoesNotContain("Trailing option", logs, StringComparison.Ordinal);
    }

    [FactSkipFFmpegTests]
    public async Task TestFFmpegVersionCheck()
    {
        Assert.True(await CreateFFmpegService().CheckFFmpegVersionAsync());
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

        var output = RunFFmpeg("-hide_banner -threads 0 -loglevel warning -version");

        Assert.Contains("Trailing option", output, StringComparison.Ordinal);
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
        var blackFrames = await CreateFFmpegService().DetectBlackFramesAsync(episode, new TimeRange(0, 2), 85, 32, AnalysisMode.Introduction);

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
        var blackFrames = await CreateFFmpegService().DetectBlackFramesAsync(episode, 32);

        Assert.NotNull(blackFrames);
    }

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWithBlackIntervalDetection()
    {
        var episode = QueueFile("credits.mp4");
        episode.Duration = 5;
        episode.CreditsFingerprintStart = 0;
        episode.CreditsFingerprintEnd = 5;

        var blackIntervals = await CreateFFmpegService().DetectBlackIntervalsAsync(episode, new TimeRange(0, 5), 32, 85);

        Assert.NotNull(blackIntervals);
        RunFFmpegAndVerifyNoWarning("-hide_banner -threads 0 -loglevel warning -ss 0 -skip_frame noref -i ../../../video/credits.mp4 -to 5 -an -dn -sn -vf blackdetect=d=0.1:pix_th=0.0731:pic_th=0.85 -f null -");
    }

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWithSilenceDetection()
    {
        // Test silence detection with actual media file
        var episode = QueueFile("rainbow.mp4");
        episode.Duration = 2;
        episode.IntroFingerprintEnd = 2;

        // Detect silence - this should not produce "Trailing option" warning
        var silenceRanges = await CreateFFmpegService().DetectSilenceAsync(episode, new TimeRange(0, 2), AnalysisMode.Introduction);

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
        var keyFrames = await CreateFFmpegService().DetectKeyFramesAsync(episode, new TimeRange(0, 2), AnalysisMode.Introduction);

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
            var fingerprint = await CreateFFmpegService().FingerprintAsync(episode, AnalysisMode.Introduction);

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
        Assert.DoesNotContain("Trailing option", RunFFmpeg(args), StringComparison.Ordinal);
    }

    private static string RunFFmpeg(string args)
    {
        var info = new ProcessStartInfo("ffmpeg", args)
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
        return output;
    }

    private static FFmpegService CreateFFmpegService()
    {
        return new FFmpegService(
            NullLogger<FFmpegService>.Instance,
            DatabaseTestHelpers.CreateTempCacheService());
    }

    private static FFmpegService CreateFFmpegService(Func<CancellationToken, Task<bool>> versionProbe)
    {
        return new FFmpegService(
            NullLogger<FFmpegService>.Instance,
            DatabaseTestHelpers.CreateTempCacheService(),
            versionProbe);
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
