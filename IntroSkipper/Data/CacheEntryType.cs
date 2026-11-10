// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
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

    /// <summary>
    /// Black interval detection results.
    /// </summary>
    BlackInterval,

    /// <summary>
    /// The pages of a keyframe scan, every keyframe to the end of the file, keyed by the credits start and an end of 0.
    /// </summary>
    KeyframePage,

    /// <summary>
    /// Where the credits cut start places a part's start, keyed by the two keyframes that bound the decoded gap.
    /// </summary>
    Cut,
}
