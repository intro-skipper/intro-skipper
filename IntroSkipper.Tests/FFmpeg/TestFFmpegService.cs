// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

/* These tests require that the host system has a version of FFmpeg installed
 * which supports both chromaprint and the "-fp_format raw" flag.
 */

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegService
{
    [Theory]
    [InlineData(0, Timeout.Infinite)]
    [InlineData(-5, Timeout.Infinite)]
    [InlineData(300, 300_000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ScanTimeoutMilliseconds_ZeroDisablesTheLimit(int seconds, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, FFmpegService.ScanTimeoutMilliseconds(seconds));
    }

    // Long enough for the helper to record its pid before the runner kills it; PowerShell starts slowly.
    private static readonly int HelperKillDelay = OperatingSystem.IsWindows() ? 2000 : 500;

    [Fact]
    public Task ProcessTimeout_KillsProcessWhileDrainingOutput()
        => AssertKillsSleepingProcessAsync((runner, path, args) => Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(path, args, timeout: HelperKillDelay).WaitAsync(TimeSpan.FromSeconds(15))));

    [Fact]
    public Task RunCapturedAsync_Timeout_KillsProcess()
        => AssertKillsSleepingProcessAsync((runner, path, args) => Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunCapturedAsync(path, args, long.MaxValue, 0, HelperKillDelay).WaitAsync(TimeSpan.FromSeconds(15))));

    [Fact]
    public Task RunCapturedAsync_Cancellation_KillsProcessWithoutTimeout()
        => AssertKillsSleepingProcessAsync(async (runner, path, args) =>
        {
            using var cancellation = new CancellationTokenSource(HelperKillDelay);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runner.RunCapturedAsync(path, args, long.MaxValue, 0, Timeout.Infinite, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15)));
        });

    // Runs a helper that records its pid and then sleeps for 30 s, and checks that the runner
    // killed it rather than waiting for it to finish.
    private static async Task AssertKillsSleepingProcessAsync(Func<FFmpegProcessRunner, string, string[], Task> run)
    {
        var pidFile = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests." + Guid.NewGuid().ToString("N") + ".pid");
        var (processPath, args) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", $"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep -Seconds 30" })
            : ("/bin/sh", new[] { "-c", $"echo $$ > '{pidFile}'; sleep 30" });

        try
        {
            await run(new FFmpegProcessRunner(NullLogger.Instance), processPath, args);

            var processId = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(processId);
                Assert.True(process.HasExited, $"Timed-out helper process {processId} is still running.");
            }
            catch (ArgumentException)
            {
                // The process may have exited before GetProcessById observed it.
            }
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

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
    public async Task CheckFFmpegVersionAsync_CancelsCallerWithoutCancelingSharedProbe()
    {
        var probeCount = 0;
        var probeToken = default(CancellationToken);
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ffmpegService = CreateFFmpegService(cancellationToken =>
        {
            probeToken = cancellationToken;
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

        // The probe runs on the service-owned lifetime, not the caller's token: one
        // waiter walking away must never cancel the shared probe.
        Assert.False(probeToken.IsCancellationRequested);

        releaseProbe.SetResult(true);
        Assert.True(await remainingCaller);
        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_HungProbe_TimesOutReturnsFalseAndRetries()
    {
        var probeCount = 0;
        var ffmpegService = CreateFFmpegService(
            _ => Interlocked.Increment(ref probeCount) == 1
                ? new TaskCompletionSource<bool>().Task // hangs forever and even ignores its token
                : Task.FromResult(true),
            versionProbeTimeout: TimeSpan.FromMilliseconds(100));

        // The service-owned lifetime must fail the attempt — an unresponsive ffmpeg may
        // not wedge the gate for the rest of the process lifetime. The timeout is a
        // failed probe, not a cancellation: every waiter observes the documented false
        // verdict rather than an OperationCanceledException their own open token never
        // caused (Entrypoint.StartAsync awaits the check unguarded during startup).
        var first = ffmpegService.CheckFFmpegVersionAsync();
        var second = ffmpegService.CheckFFmpegVersionAsync();
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(10)));

        // The timed-out attempt is the one the waiters observed, so it must also
        // publish its verdict to the support bundle instead of leaving a stale
        // (possibly "okay") snapshot contradicting the false every caller received.
        Assert.Equal("timed_out", ffmpegService.GetCheckResult().Status);

        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_ProbeException_PropagatesToAllWaitersAndResetsGate()
    {
        var probeCount = 0;
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ffmpegService = CreateFFmpegService(async _ =>
        {
            if (Interlocked.Increment(ref probeCount) == 1)
            {
                probeStarted.SetResult();
                await releaseProbe.Task;
                throw new InvalidOperationException("probe exploded");
            }

            return true;
        });

        var first = ffmpegService.CheckFFmpegVersionAsync();
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = ffmpegService.CheckFFmpegVersionAsync();

        releaseProbe.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);

        // The faulted attempt must not be cached: the next call probes again, and its
        // success is then memoized.
        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_SynchronousProbeThrow_DoesNotWedgeGate()
    {
        var probeCount = 0;
        var ffmpegService = CreateFFmpegService(_ =>
            Interlocked.Increment(ref probeCount) == 1
                ? throw new InvalidOperationException("thrown before any task exists")
                : Task.FromResult(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ffmpegService.CheckFFmpegVersionAsync());
        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_ProbeCancellationFault_ResetsGateForRetry()
    {
        var probeCount = 0;
        var ffmpegService = CreateFFmpegService(_ =>
            Interlocked.Increment(ref probeCount) == 1
                ? Task.FromCanceled<bool>(new CancellationToken(canceled: true))
                : Task.FromResult(true));

        // The caller's own token was never canceled, yet a cancellation fault inside the
        // shared probe surfaces as OperationCanceledException. The gate must treat it
        // like any other failure and retry on the next call.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ffmpegService.CheckFFmpegVersionAsync());
        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_AbandonedFailedProbe_StillResetsGate()
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
        using var cancellation = new CancellationTokenSource();

        // The only waiter walks away; the in-flight probe then fails with nobody watching.
        var abandoned = ffmpegService.CheckFFmpegVersionAsync(cancellation.Token);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        releaseProbe.SetResult(false);

        // The unobserved failure must still reset the gate. The first call may attach
        // to the completing first attempt and observe its false result, but the false
        // result and the gate reset are published atomically under the gate lock, so
        // the very next call is guaranteed to run a fresh probe.
        var result = await ffmpegService.CheckFFmpegVersionAsync();
        if (!result)
        {
            result = await ffmpegService.CheckFFmpegVersionAsync();
        }

        Assert.True(result);
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task CheckFFmpegVersionAsync_ConcurrentCallersAcrossRetries_NeverOverlapProbes_AndSuccessSticks()
    {
        var probeCount = 0;
        var inFlight = 0;
        var maxInFlight = 0;
        var ffmpegService = CreateFFmpegService(async _ =>
        {
            var current = Interlocked.Increment(ref inFlight);
            int observedMax;
            do
            {
                observedMax = Volatile.Read(ref maxInFlight);
            }
            while (current > observedMax && Interlocked.CompareExchange(ref maxInFlight, current, observedMax) != observedMax);

            var attempt = Interlocked.Increment(ref probeCount);
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
            return attempt >= 4;
        });

        // The first three attempts fail and every failure must be shared, reset and
        // retried without two probes ever running at once. A failed attempt resets the
        // gate atomically with its false result, so each burst of concurrent callers is
        // guaranteed to run at least one fresh probe: four bursts deterministically
        // reach the fourth, succeeding attempt.
        for (var round = 0; round < 4; round++)
        {
            await Task.WhenAll(
                Enumerable.Range(0, 16).Select(_ => ffmpegService.CheckFFmpegVersionAsync()))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(await ffmpegService.CheckFFmpegVersionAsync());
        Assert.Equal(4, probeCount);
        Assert.Equal(1, maxInFlight);

        // Success is sticky: another concurrent burst runs no further probes.
        var afterSuccess = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => ffmpegService.CheckFFmpegVersionAsync()))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(afterSuccess, Assert.True);
        Assert.Equal(4, probeCount);
    }

    [Fact]
    public async Task EpisodeProbeMemo_ProbesOncePerEpisodeAndInputs()
    {
        var probeCount = 0;
        var memo = new EpisodeProbeMemo<string, string>();
        Task<string?> Probe(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probeCount);
            return Task.FromResult<string?>("eng");
        }

        var episode = new QueuedEpisode();

        Assert.Equal("eng", await memo.GetAsync(episode, "a", Probe, CancellationToken.None));
        Assert.Equal("eng", await memo.GetAsync(episode, "a", Probe, CancellationToken.None));
        Assert.Equal(1, probeCount);

        // Other inputs, such as another audio language, and the next pass's episode object probe again.
        await memo.GetAsync(episode, "b", Probe, CancellationToken.None);
        await memo.GetAsync(new QueuedEpisode(), "a", Probe, CancellationToken.None);
        Assert.Equal(3, probeCount);
    }

    [Fact]
    public async Task EpisodeProbeMemo_KeepsNothingFromAFailedProbe()
    {
        var memo = new EpisodeProbeMemo<string, string>();
        var episode = new QueuedEpisode();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.Null(await memo.GetAsync(episode, "a", _ => Task.FromResult<string?>(null), CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() => memo.GetAsync(episode, "a", _ => throw new TimeoutException(), CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memo.GetAsync(episode, "a", Task.FromCanceled<string?>, cancellation.Token));

        // Each failure left the next call to probe, and the first result is then kept.
        Assert.Equal("eng", await memo.GetAsync(episode, "a", _ => Task.FromResult<string?>("eng"), CancellationToken.None));
        Assert.Equal("eng", await memo.GetAsync(episode, "a", _ => throw new InvalidOperationException("A kept result must not probe."), CancellationToken.None));

        // A kept result is no shortcut past the caller's cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memo.GetAsync(episode, "a", _ => throw new InvalidOperationException("A kept result must not probe."), cancellation.Token));
    }

    [FactSkipFFmpegTests]
    public async Task TestNoTrailingOptionsWarning()
    {
        // Run FFmpeg version check to populate ChromaprintLogs
        var ffmpegService = CreateFFmpegService();
        var result = await ffmpegService.CheckFFmpegVersionAsync();

        // Get the logs and verify no "Trailing option" warning appears
        var logs = string.Join('\n', ffmpegService.GetCheckResult().Outputs.Select(o => o.Output));

        // The test passes if FFmpeg version check succeeds (no error)
        // and no "Trailing option" warning is in the logs
        Assert.True(result, "FFmpeg version check should pass");
        Assert.DoesNotContain("Trailing option", logs, StringComparison.Ordinal);
    }

    [FactSkipFFmpegTests]
    public async Task RunCapturedAsync_StopsAtTheByteCap()
    {
        // A minute of frames is far more than the cap; the runner must kill ffmpeg at the cap and
        // report the cut rather than keep reading.
        var capture = await new FFmpegProcessRunner(NullLogger.Instance).RunCapturedAsync(
            "ffmpeg",
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=64x36:r=24:d=60", "-f", "rawvideo", "-"],
            maximumBytesPerStream: 10_000,
            expectedStdoutBytes: 10_000,
            timeout: 30_000);

        Assert.True(capture.Truncated);
        Assert.True(capture.Stdout.Length <= 10_000);
    }

    [FactSkipFFmpegTests]
    public async Task RunCapturedAsync_StopsAtTheByteCapOnStderr()
    {
        // A file that logs without producing frames: showinfo prints a line per frame to stderr and
        // the null muxer writes nothing. The cap must kill ffmpeg on stderr alone, with no timeout.
        var capture = await new FFmpegProcessRunner(NullLogger.Instance).RunCapturedAsync(
            "ffmpeg",
            ["-nostdin", "-hide_banner", "-loglevel", "info", "-f", "lavfi", "-i", "color=c=black:s=64x36:r=1000:d=60", "-vf", "showinfo", "-f", "null", "-"],
            maximumBytesPerStream: 10_000,
            expectedStdoutBytes: 0,
            timeout: Timeout.Infinite);

        Assert.True(capture.Truncated);
        Assert.True(capture.Stderr.Length <= 10_000);
    }

    [FactSkipFFmpegTests]
    public async Task ProbeAudioDurationAsync_ReadsANineDigitMatroskaDurationTag()
    {
        // ffmpeg's Matroska muxer, like mkvmerge, writes no stream duration and a DURATION tag
        // such as 00:00:03.500000000.
        var path = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-audio.mkv");
        try
        {
            await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
                "ffmpeg",
                ["-y", "-v", "error", "-f", "lavfi", "-i", "sine=d=3.5", "-c:a", "flac", path]);

            var duration = await CreateFFmpegService().ProbeAudioDurationAsync(path);

            Assert.NotNull(duration);
            Assert.Equal(3.5, duration.Value, 0.01);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FFmpegService CreateFFmpegService(Func<CancellationToken, Task<bool>>? versionProbe = null, TimeSpan? versionProbeTimeout = null)
        => FfmpegTestHelpers.CreateFFmpegService(versionProbe, versionProbeTimeout);
}
