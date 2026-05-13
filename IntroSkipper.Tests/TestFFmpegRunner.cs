// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegRunner
{
    [Fact]
    public void CreateProcessStartInfo_InfoQuery_OmitsThreadArguments()
    {
        var runner = CreateRunner(new StubOptionsProvider { ProcessThreads = 7 });

        var info = runner.CreateProcessStartInfo(["-version"], stderr: false);

        Assert.True(info.RedirectStandardOutput);
        Assert.False(info.RedirectStandardError);
        Assert.Equal(
            ["-hide_banner", "-loglevel", "warning", "-version"],
            info.ArgumentList);
    }

    [Fact]
    public void CreateProcessStartInfo_FilterQuery_UsesInfoLogLevelAndThreadArguments()
    {
        var runner = CreateRunner(new StubOptionsProvider { ProcessThreads = 7 });

        var info = runner.CreateProcessStartInfo(["-vf", "showinfo", "-f", "null", "-"], stderr: true);

        Assert.False(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(
            ["-hide_banner", "-threads", "7", "-loglevel", "info", "-vf", "showinfo", "-f", "null", "-"],
            info.ArgumentList);
    }

    [Fact]
    public async Task RunAsync_CapturesOnlySelectedOutputStream()
    {
        var runner = CreateRunner(
            processFactory: startInfo => new FakeProcess(
                startInfo,
                CreateStream("stdout"),
                CreateStream("stderr")));

        var stdout = await runner.RunAsync(["-i", "input"], stderr: false);
        var stderr = await runner.RunAsync(["-i", "input"], stderr: true);

        Assert.Equal("stdout", Encoding.UTF8.GetString(stdout.Output));
        Assert.Equal("stderr", Encoding.UTF8.GetString(stderr.Output));
    }

    [Fact]
    public async Task RunAsync_ExitTimeoutDoesNotKillProcessAndReturnsMinusOneExitCode()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            Stream.Null,
            Stream.Null,
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);

        var result = await runner.RunAsync(["-i", "input"], timeout: 10);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(process.Killed);
    }

    [Fact]
    public async Task RunAsync_ExitTimeoutReturnsWhenSelectedStreamStaysOpen()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            new CancelOnlyStream(),
            Stream.Null,
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: 10, cancellationToken: cts.Token);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(500))) == task;
        if (!completed)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        Assert.True(completed, "RunAsync should return on exit timeout even when redirected streams stay open.");
        var result = await task;
        Assert.Equal(-1, result.ExitCode);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_ExitTimeoutReturnsWhenDrainedStreamStaysOpen()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            Stream.Null,
            new CancelOnlyStream(),
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: 10, cancellationToken: cts.Token);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(500))) == task;
        if (!completed)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        Assert.True(completed, "RunAsync should return on exit timeout even when the drained stream stays open.");
        var result = await task;
        Assert.Equal(-1, result.ExitCode);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationWhileWaitingForExit_KillsProcessAndThrows()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            Stream.Null,
            Stream.Null,
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: 1000, cancellationToken: cts.Token);
        cts.CancelAfter(10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_KillsProcessAndThrows()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            new CancelOnlyStream(),
            Stream.Null,
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: 1000, cancellationToken: cts.Token);
        cts.CancelAfter(10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(process.Killed);
    }

    private static FFmpegRunner CreateRunner(
        StubOptionsProvider? options = null,
        Func<ProcessStartInfo, FFmpegRunner.IProcess>? processFactory = null)
        => new(
            options ?? new StubOptionsProvider(),
            NullLogger<FFmpegRunner>.Instance,
            processFactory ?? (static startInfo => new FakeProcess(startInfo, Stream.Null, Stream.Null)));

    private static MemoryStream CreateStream(string value) => new(Encoding.UTF8.GetBytes(value));

    private sealed class StubOptionsProvider : IFFmpegOptionsProvider
    {
        public bool CacheFingerprints => false;

        public CompressionLevel CacheCompressionLevel => CompressionLevel.Optimal;

        public string? FingerprintCachePath => null;

        public int SilenceDetectionMaximumNoise => -50;

        public string FFmpegPath => "ffmpeg";

        public int ProcessThreads { get; init; }

        public ProcessPriorityClass ProcessPriority => ProcessPriorityClass.Normal;
    }

    private sealed class FakeProcess : FFmpegRunner.IProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FakeProcess(
            ProcessStartInfo startInfo,
            Stream standardOutput,
            Stream standardError,
            bool hasExited = true)
        {
            StartInfo = startInfo;
            StandardOutput = standardOutput;
            StandardError = standardError;
            HasExited = hasExited;

            if (hasExited)
            {
                _exit.TrySetResult();
            }
        }

        public ProcessStartInfo StartInfo { get; }

        public Stream StandardOutput { get; }

        public Stream StandardError { get; }

        public bool HasExited { get; private set; }

        public int ExitCode { get; private set; }

        public bool Killed { get; private set; }

        public ProcessPriorityClass PriorityClass { private get; set; }

        public void Start()
        {
        }

        public void WaitForExit(int milliseconds)
        {
            HasExited = true;
            _exit.TrySetResult();
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Kill(bool entireProcessTree)
        {
            Killed = true;
            HasExited = true;
            _exit.TrySetResult();
        }

        public void Dispose()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }

    private sealed class CancelOnlyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
