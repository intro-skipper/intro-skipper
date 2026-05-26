// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Runs FFmpeg with the provided arguments and captures output from the selected stream.
/// </summary>
public interface IFFmpegRunner
{
    /// <summary>
    /// Runs FFmpeg asynchronously with the provided arguments and returns the captured output.
    /// </summary>
    /// <remarks>
    /// Both stdout and stderr are redirected. If <paramref name="timeout" /> elapses before FFmpeg
    /// exits, the process is killed and the result has
    /// <see cref="FFmpegProcessStatus.TimedOut" /> status. Caller cancellation via
    /// <paramref name="cancellationToken" /> also kills the process.
    /// </remarks>
    /// <param name="args">Arguments to pass to FFmpeg as individual tokens.</param>
    /// <param name="outputStream">Which output stream to capture.</param>
    /// <param name="timeout">
    /// Maximum time to wait for FFmpeg to exit, <see langword="null" /> to use the default 60-second timeout,
    /// or <see cref="Timeout.InfiniteTimeSpan" /> to wait indefinitely.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The captured selected-stream output bytes, process status, and exit code.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is negative and not <see cref="Timeout.InfiniteTimeSpan" />.</exception>
    Task<FFmpegProcessResult> RunAsync(
        IReadOnlyList<string> args,
        FFmpegOutputStream outputStream = FFmpegOutputStream.Stdout,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the supplied executable asynchronously with the provided arguments and returns the captured output.
    /// </summary>
    /// <param name="executablePath">Executable path.</param>
    /// <param name="args">Arguments to pass as individual tokens.</param>
    /// <param name="outputStream">Which output stream to capture.</param>
    /// <param name="timeout">Maximum time to wait for the process to exit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The captured selected-stream output bytes, process status, and exit code.</returns>
    Task<FFmpegProcessResult> RunExecutableAsync(
        string executablePath,
        IReadOnlyList<string> args,
        FFmpegOutputStream outputStream = FFmpegOutputStream.Stdout,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This runner cannot execute alternate FFmpeg tools.");
}
