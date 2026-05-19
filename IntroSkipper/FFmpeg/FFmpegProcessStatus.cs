// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Indicates how an FFmpeg process terminated.
/// </summary>
public enum FFmpegProcessStatus
{
    /// <summary>
    /// The process exited normally (check <see cref="FFmpegProcessResult.ExitCode"/> for the exit code).
    /// </summary>
    Completed,

    /// <summary>
    /// The process was killed because the configured timeout elapsed.
    /// <see cref="FFmpegProcessResult.ExitCode"/> is <see langword="null"/> in this state.
    /// </summary>
    TimedOut,
}
