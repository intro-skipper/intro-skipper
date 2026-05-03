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
    Chromaprint,

    /// <summary>
    /// Silence detection results.
    /// </summary>
    Silence,

    /// <summary>
    /// Black frame detection results.
    /// </summary>
    BlackFrame,

    /// <summary>
    /// Key frame timestamp data.
    /// </summary>
    Keyframe,
}
