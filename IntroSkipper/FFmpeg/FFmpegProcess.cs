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
    /// drains its output stream asynchronously, and returns the raw bytes.
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
        var info = new ProcessStartInfo(processPath)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            ErrorDialog = false,
            RedirectStandardOutput = !captureStderr,
            RedirectStandardError = captureStderr
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        _logger.LogDebug("Starting ffmpeg with the following arguments: {Arguments}", string.Join(" ", info.ArgumentList));

        using var process = new Process { StartInfo = info };
        process.Start();

        try
        {
            process.PriorityClass = priority ?? ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e)
        {
            _logger.LogWarning("ffmpeg priority could not be modified. {Message}", e.Message);
        }

        // Register cancellation: kill the entire process tree so child processes don't linger.
        // The stream read loop below will then naturally throw OperationCanceledException.
        using var cancellationRegistration = cancellationToken.Register(() => process.Kill(entireProcessTree: true));

        using var ms = new MemoryStream();
        var buffer = new byte[4096];

        // IMPORTANT: drain the stream FIRST, then call WaitForExit — do not invert this order.
        var stream = captureStderr ? process.StandardError.BaseStream : process.StandardOutput.BaseStream;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await ms.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("ffmpeg did not exit within {TimeoutMs}ms; killing process", timeoutMs);
            process.Kill(entireProcessTree: true);
        }

        return ms.ToArray();
    }
}
