// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides live, read-through access to FFmpeg process configuration.
/// Each property is evaluated at call time so callers always see current settings.
/// </summary>
public interface IFFmpegProcessOptions
{
    /// <summary>
    /// Gets the full path to the FFmpeg executable.
    /// </summary>
    string FFmpegPath { get; }

    /// <summary>
    /// Gets the number of threads FFmpeg should use (0 = auto).
    /// </summary>
    int ProcessThreads { get; }

    /// <summary>
    /// Gets the process priority class for FFmpeg processes.
    /// </summary>
    ProcessPriorityClass ProcessPriority { get; }
}
