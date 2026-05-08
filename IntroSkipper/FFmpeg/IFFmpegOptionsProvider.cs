// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.IO.Compression;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides live, read-through access to FFmpeg-related plugin configuration.
/// Each property is evaluated at call time so callers always see current settings.
/// </summary>
public interface IFFmpegOptionsProvider
{
    /// <summary>
    /// Gets a value indicating whether fingerprint caching is enabled.
    /// </summary>
    bool CacheFingerprints { get; }

    /// <summary>
    /// Gets the Brotli compression level used for cache entries.
    /// </summary>
    CompressionLevel CacheCompressionLevel { get; }

    /// <summary>
    /// Gets the path to the legacy on-disk fingerprint cache directory, or <c>null</c> if unavailable.
    /// </summary>
    string? FingerprintCachePath { get; }

    /// <summary>
    /// Gets the maximum noise level (in dB) for silence detection.
    /// </summary>
    int SilenceDetectionMaximumNoise { get; }

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
