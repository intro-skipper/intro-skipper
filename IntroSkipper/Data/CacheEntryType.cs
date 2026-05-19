// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Type of detection data stored in a cache entry.
/// </summary>
public enum CacheEntryType
{
    /// <summary>
    /// Audio fingerprint data (Chromaprint).
    /// </summary>
    Chromaprint = 0,

    /// <summary>
    /// Silence detection results.
    /// </summary>
    Silence = 1,

    /// <summary>
    /// Blackframe detection results.
    /// </summary>
    BlackFrame = 2,

    /// <summary>
    /// Key frame timestamp data.
    /// </summary>
    Keyframe = 3,

    /// <summary>
    /// Blackframe detection results generated from downscaled analysis frames.
    /// </summary>
    BlackFrameScaled480 = 4,
}
