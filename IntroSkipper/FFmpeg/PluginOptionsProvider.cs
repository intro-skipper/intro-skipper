// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.IO.Compression;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Reads FFmpeg and detection configuration from <see cref="Plugin.Instance"/> on each property access.
/// Registered as a singleton and injected directly into FFmpeg service consumers.
/// </summary>
public class PluginOptionsProvider : IPluginOptionsProvider
{
    /// <summary>Gets the path to the FFmpeg executable.</summary>
    public virtual string FFmpegPath => Plugin.Instance?.FFmpegPath ?? "ffmpeg";

    /// <summary>Gets the number of threads for FFmpeg process execution.</summary>
    public virtual int ProcessThreads => Plugin.Instance?.Configuration.ProcessThreads ?? 0;

    /// <summary>Gets the process priority for FFmpeg processes.</summary>
    public virtual ProcessPriorityClass ProcessPriority => Plugin.Instance?.Configuration.ProcessPriority ?? ProcessPriorityClass.BelowNormal;

    /// <summary>Gets a value indicating whether fingerprint caching is enabled.</summary>
    public virtual bool CacheFingerprints => Plugin.Instance?.Configuration.CacheFingerprints ?? false;

    /// <summary>Gets the compression level for cached fingerprint data.</summary>
    public virtual CompressionLevel CacheCompressionLevel => Plugin.Instance?.Configuration.CacheCompressionLevel ?? CompressionLevel.Optimal;

    /// <summary>Gets the path to the fingerprint cache directory.</summary>
    public virtual string? FingerprintCachePath => Plugin.Instance?.FingerprintCachePath;

    /// <summary>Gets the maximum noise level (in dB) for silence detection.</summary>
    public virtual int SilenceDetectionMaximumNoise => Plugin.Instance?.Configuration.SilenceDetectionMaximumNoise ?? -50;

    /// <summary>Gets the black frame detection threshold.</summary>
    public virtual int BlackFrameThreshold => Plugin.Instance?.Configuration.BlackFrameThreshold ?? 28;
}
