// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.IO.Compression;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides live, read-through access to detection cache configuration.
/// Each property is evaluated at call time so callers always see current settings.
/// </summary>
public interface IDetectionCacheOptions
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
}
