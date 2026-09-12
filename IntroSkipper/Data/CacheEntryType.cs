// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace IntroSkipper.Data;

/// <summary>
/// Type of detection data stored in a cache entry. Stored as its integer value, so the
/// values are pinned. Value 3 was the keyframe listing, now read from Jellyfin's keyframe
/// store; rows of that type are never read.
/// </summary>
[SuppressMessage("Design", "CA1027:Mark enums with FlagsAttribute", Justification = "Pinned storage identifiers with a retired value, not flags.")]
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
    /// Black frame detection results.
    /// </summary>
    BlackFrame = 2,

    /// <summary>
    /// Black interval detection results.
    /// </summary>
    BlackInterval = 4,

    /// <summary>
    /// Per-keyframe visual statistics (entropy and saturation) for non-black credit detection.
    /// </summary>
    KeyframeVisual = 5,
}
