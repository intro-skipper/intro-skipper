// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
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

            using var ms = new MemoryStream();
            // Draining must not use the caller token: on cancellation or timeout the process is
            // killed first, then its remaining output is drained so the pipes cannot deadlock.
            var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, stderr ? null : ms);
            var stderrTask = DrainAsync(process.StandardError.BaseStream, stderr ? ms : null);

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

            cancellationToken.ThrowIfCancellationRequested();
            if (timedOut)
            {
                throw new TimeoutException($"ffmpeg process was killed after not exiting within {timeout}ms");
            }

            return ms.ToArray();
        }
        finally
        {
            // Only reachable with a live process when draining or the exit wait itself faulted.
            KillProcessTree(process);
        }
    }

    private static async Task DrainAsync(Stream stream, Stream? destination)
    {
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (destination is not null)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
            }
        }
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
