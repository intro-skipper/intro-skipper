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
    /// exits, the process is killed and the result contains any selected-stream output captured so
    /// far with an exit code of <c>-1</c>. Caller cancellation via
    /// <paramref name="cancellationToken" /> also kills the process.
    /// </remarks>
    /// <param name="args">Arguments to pass to FFmpeg as individual tokens.</param>
    /// <param name="stderr">If <c>true</c>, capture standard error; otherwise capture standard output.</param>
    /// <param name="timeout">Timeout in milliseconds to wait for FFmpeg to exit, or <see cref="Timeout.Infinite" /> to wait indefinitely.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The captured selected-stream output bytes and process exit code.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout" /> is less than <see cref="Timeout.Infinite" />.</exception>
    Task<FFmpegProcessResult> RunAsync(IReadOnlyList<string> args, bool stderr = false, int timeout = 60 * 1000, CancellationToken cancellationToken = default);
}
