// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace IntroSkipper.Data;

/// <summary>
/// Shared constants for chromaprint fingerprint timing calculations.
/// </summary>
/// <remarks>
/// Chromaprint parameters (sample rate 11025 Hz, frame size 4096, 2/3 overlap)
/// define the relationship between fingerprint point count and audio duration.
/// See: <see href="https://oxygene.sk/2011/01/how-does-chromaprint-work/"/>.
/// </remarks>
public static class ChromaprintConstants
{
    /// <summary>
    /// Duration in seconds of one fingerprint point (hop duration).
    /// Computed as <c>4096 / (11025 * 3)</c> ≈ 0.12383 seconds.
    /// </summary>
    public const double SampleDuration = 4096.0 / 11025.0 / 3.0;

    /// <summary>
    /// Duration in seconds of the analysis window that extends beyond the last hop.
    /// Each 32-bit hash covers approximately 2.6 seconds of audio (~21 chroma frames).
    /// </summary>
    public const double HashWindowDuration = 2.6;

    /// <summary>
    /// Maximum acceptable difference in seconds between inferred and expected fingerprint duration
    /// during legacy cache migration crosscheck.
    /// </summary>
    public const double DurationTolerance = 5.0;

    /// <summary>
    /// Infers the audio duration (in whole seconds) from a fingerprint point count.
    /// </summary>
    /// <param name="lineCount">Number of fingerprint points (uint values).</param>
    /// <returns>Estimated duration in seconds, rounded to the nearest whole second.</returns>
    public static double InferDuration(int lineCount)
    {
        return Math.Round((lineCount * SampleDuration) + HashWindowDuration);
    }
}
