// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Describes the detection operation associated with a cache entry.
/// </summary>
public enum DetectionCacheKind
{
    /// <summary>Silence detection over a bounded range.</summary>
    Silence,

    /// <summary>Black-frame detection over a bounded range.</summary>
    BlackFrameRange,

    /// <summary>Alternate black-frame detection for credits fingerprinting.</summary>
    BlackFrameAlt,

    /// <summary>Keyframe detection over a bounded range.</summary>
    Keyframe
}
