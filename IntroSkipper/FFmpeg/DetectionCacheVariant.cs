// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Builds versioned detection-cache variant identifiers.
/// </summary>
public static class DetectionCacheVariant
{
    private const string AbsoluteV2 = "absolute-v2";

    /// <summary>Gets the chromaprint cache variant.</summary>
    /// <returns>The chromaprint cache variant.</returns>
    public static string Chromaprint() => "chromaprint:v1";

    /// <summary>Gets the silence cache variant.</summary>
    /// <param name="maxNoise">The configured maximum noise level.</param>
    /// <returns>The silence cache variant.</returns>
    public static string Silence(int maxNoise) => $"silence:{AbsoluteV2}:noise={maxNoise}";

    /// <summary>Gets the keyframe cache variant.</summary>
    /// <returns>The keyframe cache variant.</returns>
    public static string Keyframe() => $"keyframe:{AbsoluteV2}";

    /// <summary>Gets the range blackframe cache variant.</summary>
    /// <param name="threshold">The configured blackframe threshold.</param>
    /// <returns>The range blackframe cache variant.</returns>
    public static string BlackFrameRange(int threshold) => $"blackframe-range:{AbsoluteV2}:amount=50:threshold={threshold}";

    /// <summary>Gets the credits blackframe cache variant.</summary>
    /// <param name="threshold">The configured blackframe threshold.</param>
    /// <returns>The credits blackframe cache variant.</returns>
    public static string BlackFrameCredits(int threshold) => $"blackframe-credits:{AbsoluteV2}:keyframes-only:amount=0:threshold={threshold}";
}
