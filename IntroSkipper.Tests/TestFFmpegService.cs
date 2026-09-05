// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
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
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegService
{
    [Fact]
    public async Task ProcessTimeout_KillsProcessWhileDrainingOutput()
    {
        var pidFile = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests." + Guid.NewGuid().ToString("N") + ".pid");
        var processPath = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
        string[] args;
        int timeout;
        if (OperatingSystem.IsWindows())
        {
            args = ["-NoProfile", "-NonInteractive", "-Command", $"Set-Content -LiteralPath '{pidFile}' -Value $PID; Start-Sleep -Seconds 30"];
            timeout = 2000;
        }
        else
        {
            args = ["-c", $"echo $$ > '{pidFile}'; sleep 30"];
            timeout = 500;
        }

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => new FFmpegProcessRunner(NullLogger.Instance)
                .RunAsync(processPath, args, timeout: timeout)
                .WaitAsync(TimeSpan.FromSeconds(15)));

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

    private static FFmpegService CreateFFmpegService(Func<CancellationToken, Task<bool>>? versionProbe = null, TimeSpan? versionProbeTimeout = null)
        => FfmpegTestHelpers.CreateFFmpegService(versionProbe, versionProbeTimeout);
}
