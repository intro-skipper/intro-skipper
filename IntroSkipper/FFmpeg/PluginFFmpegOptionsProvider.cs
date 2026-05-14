// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.IO.Compression;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Reads FFmpeg-related configuration from <see cref="Plugin.Instance"/> on each property access.
/// </summary>
public sealed class PluginFFmpegOptionsProvider : IFFmpegOptionsProvider
{
    /// <inheritdoc />
    public bool CacheFingerprints => Plugin.Instance?.Configuration.CacheFingerprints ?? false;

    /// <inheritdoc />
    public CompressionLevel CacheCompressionLevel => Plugin.Instance?.Configuration.CacheCompressionLevel ?? CompressionLevel.Optimal;

    /// <inheritdoc />
    public string? FingerprintCachePath => Plugin.Instance?.FingerprintCachePath;

    /// <inheritdoc />
    public int SilenceDetectionMaximumNoise => Plugin.Instance?.Configuration.SilenceDetectionMaximumNoise ?? -50;

    /// <inheritdoc />
    public string FFmpegPath => Plugin.Instance?.FFmpegPath ?? "ffmpeg";

    /// <inheritdoc />
    public int ProcessThreads => Plugin.Instance?.Configuration.ProcessThreads ?? 0;

    /// <inheritdoc />
    public ProcessPriorityClass ProcessPriority => Plugin.Instance?.Configuration.ProcessPriority ?? ProcessPriorityClass.BelowNormal;
}
