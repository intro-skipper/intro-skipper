// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
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
        var runner = CreateRunner(new StubOptionsProvider { TestProcessThreads = 7 });

        var info = runner.CreateProcessStartInfo(["-version"]);

        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(
            ["-hide_banner", "-loglevel", "warning", "-version"],
            info.ArgumentList);
    }

    [Fact]
    public void CreateProcessStartInfo_FilterQuery_UsesInfoLogLevelAndThreadArguments()
    {
        var runner = CreateRunner(new StubOptionsProvider { TestProcessThreads = 7 });

        var info = runner.CreateProcessStartInfo(["-vf", "showinfo", "-f", "null", "-"]);

        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(
            ["-hide_banner", "-threads", "7", "-loglevel", "info", "-vf", "showinfo", "-f", "null", "-"],
            info.ArgumentList);
    }

    [Fact]
    public async Task RunFFprobeAsync_DoesNotPrependFfmpegArguments()
    {
        ProcessStartInfo? captured = null;
        var runner = CreateRunner(processFactory: startInfo =>
        {
            captured = startInfo;
            return new FakeProcess(startInfo, CreateStream("123"), Stream.Null);
        });

        var result = await runner.RunFFprobeAsync(["-v", "error", "input.mkv"]);

        Assert.Equal(FFmpegProcessStatus.Completed, result.Status);
        Assert.NotNull(captured);
        Assert.Equal(["-v", "error", "input.mkv"], captured.ArgumentList);
        Assert.DoesNotContain("-threads", captured.ArgumentList);
    }

    [Fact]
    public async Task RunAsync_CapturesOnlySelectedOutputStream()
    {
        var runner = CreateRunner(
            processFactory: startInfo => new FakeProcess(
                startInfo,
                CreateStream("stdout"),
                CreateStream("stderr")));

        var stdout = await runner.RunAsync(["-i", "input"], FFmpegOutputStream.Stdout);
        var stderr = await runner.RunAsync(["-i", "input"], FFmpegOutputStream.Stderr);

        Assert.Equal("stdout", Encoding.UTF8.GetString(stdout.Output));
        Assert.Equal("stderr", Encoding.UTF8.GetString(stderr.Output));
    }

    [Fact]
    public async Task RunAsync_CompletedProcess_PreservesNonzeroExitCodeAndBothStreams()
    {
        var runner = CreateRunner(
            processFactory: startInfo => new FakeProcess(
                startInfo,
                CreateStream("stdout"),
                CreateStream("stderr"),
                exitCode: 1));

        var result = await runner.RunAsync(["-i", "input"], FFmpegOutputStream.Stdout);

        Assert.Equal(FFmpegProcessStatus.Completed, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("stdout", Encoding.UTF8.GetString(result.Output));
        Assert.Equal("stderr", Encoding.UTF8.GetString(result.DrainedOutput));
    }

    [Fact]
    public async Task RunAsync_NegativeTimeoutOtherThanInfinite_ThrowsBeforeStartingProcess()
    {
        var factoryCalled = false;
        var runner = CreateRunner(processFactory: startInfo =>
        {
            factoryCalled = true;
            return new FakeProcess(startInfo, Stream.Null, Stream.Null);
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runner.RunAsync(["-i", "input"], timeout: TimeSpan.FromMilliseconds(-2)));

        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task RunAsync_InfiniteTimeout_IsAccepted()
    {
        var runner = CreateRunner(
            processFactory: startInfo => new FakeProcess(
                startInfo,
                CreateStream("stdout"),
                Stream.Null));

        var result = await runner.RunAsync(["-i", "input"], timeout: Timeout.InfiniteTimeSpan);

        Assert.Equal(FFmpegProcessStatus.Completed, result.Status);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_TooLargeTimeout_ThrowsBeforeStartingProcess()
    {
        var factoryCalled = false;
        var runner = CreateRunner(processFactory: startInfo =>
        {
            factoryCalled = true;
            return new FakeProcess(startInfo, Stream.Null, Stream.Null);
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => runner.RunAsync(["-i", "input"], timeout: TimeSpan.MaxValue));

        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task RunAsync_WhenStreamsCloseBeforeProcessExits_ReturnsTimedOutStatus()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            Stream.Null,
            Stream.Null,
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);

        var result = await runner.RunAsync(["-i", "input"], timeout: TimeSpan.FromMilliseconds(10));

        Assert.Equal(FFmpegProcessStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_ExitTimeoutReturnsWhenSelectedStreamStaysOpen()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            new BlockingReadStream(),
            Stream.Null,
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: TimeSpan.FromMilliseconds(10), cancellationToken: cts.Token);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(500))) == task;
        if (!completed)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        Assert.True(completed, "RunAsync should return on exit timeout even when redirected streams stay open.");
        var result = await task;
        Assert.Equal(FFmpegProcessStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_ExitTimeoutReturnsWhenDrainedStreamStaysOpen()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            Stream.Null,
            new BlockingReadStream(),
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: TimeSpan.FromMilliseconds(10), cancellationToken: cts.Token);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(500))) == task;
        if (!completed)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        Assert.True(completed, "RunAsync should return on exit timeout even when the drained stream stays open.");
        var result = await task;
        Assert.Equal(FFmpegProcessStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_ExitTimeoutReturnsWhenSelectedStreamKeepsProducingOutput()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            new RepeatingReadStream(),
            Stream.Null,
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: TimeSpan.FromMilliseconds(10), cancellationToken: cts.Token);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(500))) == task;
        if (!completed)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        Assert.True(completed, "RunAsync should return on exit timeout even when selected output keeps arriving.");
        var result = await task;
        Assert.Equal(FFmpegProcessStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
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

        var task = runner.RunAsync(["-i", "input"], timeout: TimeSpan.FromMilliseconds(1000), cancellationToken: cts.Token);
        cts.CancelAfter(10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_KillsProcessAndThrows()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            new BlockingReadStream(),
            Stream.Null,
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: TimeSpan.FromMilliseconds(1000), cancellationToken: cts.Token);
        cts.CancelAfter(10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationWithFaultedDrainedStream_KillsProcessAndThrowsCancellation()
    {
        var process = new FakeProcess(
            new ProcessStartInfo("ffmpeg"),
            new BlockingReadStream(),
            new BlockingReadStream(faultOnCancellation: true),
            hasExited: false);
        var runner = CreateRunner(processFactory: _ => process);
        using var cts = new CancellationTokenSource();

        var task = runner.RunAsync(["-i", "input"], timeout: TimeSpan.FromMilliseconds(1000), cancellationToken: cts.Token);
        cts.CancelAfter(10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task RunAsync_ProcessStartThrows_DisposesProcess()
    {
        var disposed = false;
        var runner = CreateRunner(processFactory: startInfo =>
        {
            var process = new ThrowOnStartProcess(startInfo, onDispose: () => disposed = true);
            return process;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(["-i", "input"]));

        Assert.True(disposed, "Process must be disposed when Start() throws");
    }

    [Fact]
    public async Task RunAsync_StdoutSelected_DrainedOutputContainsStderr()
    {
        var runner = CreateRunner(
            processFactory: startInfo => new FakeProcess(
                startInfo,
                CreateStream("stdout-data"),
                CreateStream("stderr-data")));

        var result = await runner.RunAsync(["-i", "input"], FFmpegOutputStream.Stdout);

        Assert.Equal("stdout-data", Encoding.UTF8.GetString(result.Output));
        Assert.Equal("stderr-data", Encoding.UTF8.GetString(result.DrainedOutput));
    }

    [Fact]
    public async Task RunAsync_StderrSelected_DrainedOutputContainsStdout()
    {
        var runner = CreateRunner(
            processFactory: startInfo => new FakeProcess(
                startInfo,
                CreateStream("stdout-data"),
                CreateStream("stderr-data")));

        var result = await runner.RunAsync(["-i", "input"], FFmpegOutputStream.Stderr);

        Assert.Equal("stderr-data", Encoding.UTF8.GetString(result.Output));
        Assert.Equal("stdout-data", Encoding.UTF8.GetString(result.DrainedOutput));
    }

    private static FFmpegRunner CreateRunner(
        StubOptionsProvider? options = null,
        Func<ProcessStartInfo, FFmpegRunner.IProcess>? processFactory = null)
        => new(
            options ?? new StubOptionsProvider(),
            NullLogger<FFmpegRunner>.Instance,
            processFactory ?? (static startInfo => new FakeProcess(startInfo, Stream.Null, Stream.Null)));

    private static MemoryStream CreateStream(string value) => new(Encoding.UTF8.GetBytes(value));

    private sealed class StubOptionsProvider : PluginOptionsProvider
    {
        public override string FFmpegPath => "ffmpeg";

        public int TestProcessThreads { get; set; }

        public override int ProcessThreads => TestProcessThreads;

        public override ProcessPriorityClass ProcessPriority => ProcessPriorityClass.Normal;
    }

    private sealed class FakeProcess : FFmpegRunner.IProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FakeProcess(
            ProcessStartInfo startInfo,
            Stream standardOutput,
            Stream standardError,
            bool hasExited = true,
            int exitCode = 0)
        {
            StartInfo = startInfo;
            StandardOutput = standardOutput;
            StandardError = standardError;
            HasExited = hasExited;
            ExitCode = exitCode;

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

    private sealed class RepeatingReadStream : Stream
    {
        private static readonly byte[] Chunk = Encoding.UTF8.GetBytes("ffmpeg-output");

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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var bytesToCopy = Math.Min(buffer.Length, Chunk.Length);
            Chunk.AsMemory(0, bytesToCopy).CopyTo(buffer);
            return bytesToCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream(bool faultOnCancellation = false) : Stream
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
            await ReadUntilCanceledAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await ReadUntilCanceledAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async Task DelayUntilCanceledAsync(CancellationToken cancellationToken)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

        private async Task ReadUntilCanceledAsync(CancellationToken cancellationToken)
        {
            if (!faultOnCancellation)
            {
                await DelayUntilCanceledAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await DelayUntilCanceledAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Simulated drained-stream fault after cancellation");
            }
        }
    }

    private sealed class ThrowOnStartProcess : FFmpegRunner.IProcess
    {
        private readonly Action _onDispose;

        internal ThrowOnStartProcess(ProcessStartInfo startInfo, Action onDispose)
        {
            StartInfo = startInfo;
            _onDispose = onDispose;
        }

        public ProcessStartInfo StartInfo { get; }

        public Stream StandardOutput => Stream.Null;

        public Stream StandardError => Stream.Null;

        public bool HasExited => false;

        public int ExitCode => 0;

        public ProcessPriorityClass PriorityClass { set { } }

        public void Start() => throw new InvalidOperationException("Simulated start failure");

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Kill(bool entireProcessTree) { }

        public void Dispose() => _onDispose();
    }
}
