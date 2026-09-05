// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Shared constants for chromaprint fingerprint timing calculations.
/// </summary>
/// <remarks>
/// Chromaprint parameters (sample rate 11025 Hz, frame size 4096, 2/3 overlap)
/// define the relationship between fingerprint point count and audio duration.
/// See: <see href="https://oxygene.sk/2011/01/how-does-chromaprint-work/"/>.
/// </remarks>
internal static class ChromaprintConstants
{
    /// <summary>
    /// Duration in seconds of one fingerprint point (hop duration).
    /// Computed as <c>4096 / (11025 * 3)</c> ≈ 0.12383 seconds.
    /// </summary>
    public const double SampleDuration = 4096.0 / 11025.0 / 3.0;
}
