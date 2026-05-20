// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.IO.Compression;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Reads FFmpeg and detection configuration from the plugin instance.
/// </summary>
public interface IPluginOptionsProvider
{
    /// <summary>Gets the path to the FFmpeg executable.</summary>
    string FFmpegPath { get; }

    /// <summary>Gets the number of threads for FFmpeg process execution.</summary>
    int ProcessThreads { get; }

    /// <summary>Gets the process priority for FFmpeg processes.</summary>
    ProcessPriorityClass ProcessPriority { get; }

    /// <summary>Gets a value indicating whether fingerprint caching is enabled.</summary>
    bool CacheFingerprints { get; }

    /// <summary>Gets the compression level for cached fingerprint data.</summary>
    CompressionLevel CacheCompressionLevel { get; }

    /// <summary>Gets the path to the fingerprint cache directory.</summary>
    string? FingerprintCachePath { get; }

    /// <summary>Gets the maximum noise level (in dB) for silence detection.</summary>
    int SilenceDetectionMaximumNoise { get; }

    /// <summary>Gets the black frame detection threshold.</summary>
    int BlackFrameThreshold { get; }
}
