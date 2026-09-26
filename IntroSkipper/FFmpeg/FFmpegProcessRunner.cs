// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Spawns an ffmpeg or ffprobe process, drains its output and kills the whole process tree
/// on cancellation or timeout.
/// </summary>
/// <param name="logger">Logger.</param>
internal sealed partial class FFmpegProcessRunner(ILogger logger)
{
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Runs a process to completion and returns one of its output streams.
    /// </summary>
    /// <param name="processPath">Executable to start.</param>
    /// <param name="args">Arguments, one token each.</param>
    /// <param name="stderr"><see langword="true"/> to return standard error; otherwise standard output is returned.</param>
    /// <param name="timeout">Milliseconds to wait for the process to exit before killing it.</param>
    /// <param name="cancellationToken">Cancels the wait and kills the process.</param>
    /// <returns>The raw bytes of the selected stream.</returns>
    /// <exception cref="TimeoutException">The process did not exit within <paramref name="timeout"/> and was killed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the process has been killed.</exception>
    public async Task<byte[]> RunAsync(
        string processPath,
        IReadOnlyList<string> args,
        bool stderr = false,
        int timeout = 60 * 1000,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await RunCoreAsync(processPath, args, stderr ? null : ms, stderr ? ms : null, long.MaxValue, timeout, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>
    /// Runs a process to completion and returns both output streams and the exit code. Each stream
    /// is capped: the moment either crosses <paramref name="maximumBytesPerStream"/> the process is
    /// killed and the capture says so, so neither a decode that writes more than expected nor a
    /// process that only logs can fill memory, whatever <paramref name="timeout"/> allows.
    /// </summary>
    /// <param name="processPath">Executable to start.</param>
    /// <param name="args">Arguments, one token each.</param>
    /// <param name="maximumBytesPerStream">Bytes of each output stream kept before the process is killed.</param>
    /// <param name="expectedStdoutBytes">Bytes of standard output the caller expects, to size the buffer once; clamped to <paramref name="maximumBytesPerStream"/>.</param>
    /// <param name="timeout">Milliseconds to wait for the process to exit before killing it, or <see cref="Timeout.Infinite"/>.</param>
    /// <param name="cancellationToken">Cancels the wait and kills the process.</param>
    /// <returns>Both streams, the exit code and whether a stream was cut.</returns>
    /// <exception cref="TimeoutException">The process did not exit within <paramref name="timeout"/> and was killed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the process has been killed.</exception>
    public async Task<ProcessCapture> RunCapturedAsync(
        string processPath,
        IReadOnlyList<string> args,
        long maximumBytesPerStream,
        long expectedStdoutBytes,
        int timeout,
        CancellationToken cancellationToken = default)
    {
        using var stdout = new MemoryStream((int)Math.Clamp(expectedStdoutBytes, 0, maximumBytesPerStream));
        using var stderr = new MemoryStream();
        var (exitCode, truncated) = await RunCoreAsync(processPath, args, stdout, stderr, maximumBytesPerStream, timeout, cancellationToken).ConfigureAwait(false);

        // A decode is tens of megabytes: hand out the stream's own buffer instead of copying it.
        // Disposing a MemoryStream releases nothing, so the buffer stays valid after the return.
        return new ProcessCapture(stdout.GetBuffer().AsMemory(0, (int)stdout.Length), Encoding.UTF8.GetString(stderr.GetBuffer(), 0, (int)stderr.Length), exitCode, truncated);
    }

    private async Task<(int ExitCode, bool Truncated)> RunCoreAsync(
        string processPath,
        IReadOnlyList<string> args,
        Stream? stdoutSink,
        Stream? stderrSink,
        long maximumBytesPerStream,
        int timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new ProcessStartInfo(processPath)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            ErrorDialog = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Starting ffmpeg with the following arguments: {Arguments}", string.Join(" ", info.ArgumentList));
        }

        using var process = new Process { StartInfo = info };
        process.Start();

        try
        {
            try
            {
                process.PriorityClass = Plugin.Instance?.Configuration.ProcessPriority ?? ProcessPriorityClass.BelowNormal;
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                LogFfmpegPriorityNotModified(_logger, e.Message);
            }

            // Draining must not use the caller token: on cancellation or timeout the process is
            // killed first, then its remaining output is drained so the pipes cannot deadlock.
            var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, stdoutSink, maximumBytesPerStream, () => KillProcessTree(process));
            var stderrTask = DrainAsync(process.StandardError.BaseStream, stderrSink, maximumBytesPerStream, () => KillProcessTree(process));

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                KillProcessTree(process);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                LogFfmpegExitTimeout(_logger, timeout);
                KillProcessTree(process);
                timedOut = true;
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            var truncated = await stdoutTask.ConfigureAwait(false) | await stderrTask.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut)
            {
                throw new TimeoutException($"ffmpeg process was killed after not exiting within {timeout}ms");
            }

            // Observed only for the single-stream callers: they parse whatever was written and
            // cache the result, and a nonzero exit does not yet say whether that output was usable
            // (a file without an audio stream fails a fingerprint run this way). Callers that take
            // the capture read the exit code themselves.
            if (process.ExitCode != 0 && _logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("ffmpeg exited with code {ExitCode}: {Arguments}", process.ExitCode, string.Join(" ", info.ArgumentList));
            }

            return (process.ExitCode, truncated);
        }
        finally
        {
            // Only reachable with a live process when draining or the exit wait itself faulted.
            KillProcessTree(process);
        }
    }

    // Reads the stream to its end. Bytes go to the destination until the limit is crossed; then
    // onLimit runs once and the rest is read and dropped so the process can still exit. A stream
    // without a destination is dropped uncounted.
    private static async Task<bool> DrainAsync(Stream stream, Stream? destination, long limit, Action onLimit)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        var truncated = false;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (destination is null || truncated)
            {
                continue;
            }

            total += bytesRead;
            if (total > limit)
            {
                truncated = true;
                onLimit();
                continue;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
        }

        return truncated;
    }

    private void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            LogFfmpegProcessAlreadyGone(_logger, ex);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or AggregateException)
        {
            // Kill(entireProcessTree: true) reports partial failures as an AggregateException.
            LogFfmpegKillFailed(_logger, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            LogFfmpegKillNotSupported(_logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffmpeg priority could not be modified. {Message}")]
    private static partial void LogFfmpegPriorityNotModified(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffmpeg did not exit within {TimeoutMs}ms; killing process")]
    private static partial void LogFfmpegExitTimeout(ILogger logger, int timeoutMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to kill ffmpeg process tree: {Message}")]
    private static partial void LogFfmpegKillFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Killing the ffmpeg process tree is not supported on this platform: {Message}")]
    private static partial void LogFfmpegKillNotSupported(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ffmpeg process already gone while killing process tree")]
    private static partial void LogFfmpegProcessAlreadyGone(ILogger logger, Exception ex);
}
