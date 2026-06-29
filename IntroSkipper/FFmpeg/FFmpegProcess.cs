// SPDX-FileCopyrightText: 2024-2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Wraps <see cref="Process.Start(ProcessStartInfo)"/> with async I/O and <see cref="CancellationToken"/> support.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FFmpegProcess"/> class.
/// </remarks>
/// <param name="logger">Logger used for diagnostic output.</param>
public sealed class FFmpegProcess(ILogger logger)
{
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Starts the process at <paramref name="processPath"/> with the given <paramref name="args"/>,
    /// drains both output streams asynchronously, and returns the raw bytes from the selected stream.
    /// </summary>
    /// <param name="processPath">Full path to the executable.</param>
    /// <param name="args">Argument list passed verbatim to the process.</param>
    /// <param name="captureStderr">
    /// When <see langword="true"/>, stderr is captured; otherwise stdout is captured.
    /// </param>
    /// <param name="timeoutMs">
    /// Milliseconds to wait for the process to exit after the output stream is drained.
    /// If the process has not exited within this window it is killed and a warning is logged.
    /// </param>
    /// <param name="priority">
    /// Optional priority class to apply to the process after it starts.
    /// Failures to set the priority are logged as warnings and are otherwise ignored.
    /// </param>
    /// <param name="cancellationToken">
    /// Token that, when cancelled, kills the entire process tree and causes the method to throw
    /// <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>The raw bytes written to the captured stream.</returns>
    public async Task<byte[]> RunAsync(
        string processPath,
        IReadOnlyList<string> args,
        bool captureStderr = false,
        int timeoutMs = 60_000,
        ProcessPriorityClass? priority = null,
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
            process.PriorityClass = priority ?? ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _logger.LogWarning("ffmpeg priority could not be modified. {Message}", e.Message);
        }

        // Register cancellation: kill the entire process tree so child processes don't linger.
        // The stream read loop below will then naturally throw OperationCanceledException.
        using var cancellationRegistration = cancellationToken.Register(() => KillProcessTree(process));

        using var ms = new MemoryStream();

        // IMPORTANT: drain streams FIRST, then call WaitForExit — do not invert this order.
        // Drain the unselected stream too so expected ffmpeg failures do not leak to test stderr.
        var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, captureStderr ? null : ms, cancellationToken);
        var stderrTask = DrainAsync(process.StandardError.BaseStream, captureStderr ? ms : null, cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("ffmpeg did not exit within {TimeoutMs}ms; killing process", timeoutMs);
            KillProcessTree(process);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ms.ToArray();
    }

    private static async Task DrainAsync(Stream stream, Stream? destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (destination is not null)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
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
            // The process exited or was disposed between the HasExited check and Kill.
            _logger.LogDebug("ffmpeg process already gone while killing process tree: {Message}", ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning("Failed to kill ffmpeg process tree: {Message}", ex.Message);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning("Killing the ffmpeg process tree is not supported on this platform: {Message}", ex.Message);
        }
    }
}
