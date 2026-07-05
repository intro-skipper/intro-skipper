// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Shared black-frame threshold normalization helpers.
/// </summary>
internal static class BlackFrameThresholdHelper
{
    /// <summary>
    /// Normalizes black-frame thresholds against the darkest frames in a scan.
    /// </summary>
    /// <param name="frames">The black-frame scan results.</param>
    /// <param name="minimumPercentage">The configured minimum black percentage.</param>
    /// <returns>The normalized black-frame and scene-change thresholds.</returns>
    internal static (int Minimum, int SceneChange) NormalizeThreshold(
        IReadOnlyList<BlackFrame> frames,
        int minimumPercentage)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count, nameof(frames));

        var orderedFrames = frames.OrderBy(f => f.Percentage).ToList();
        // Clamp into range: for short/sparse scans, frames.Count * 0.01 floors to 0,
        // so the floor becomes the single least-black frame. The 30-cap bounds that
        // frame's influence.
        var percentileIndex = Math.Clamp((int)(frames.Count * 0.01), 0, frames.Count - 1);
        var floor = Math.Min(orderedFrames[percentileIndex].Percentage, 30);
        var minimum = (minimumPercentage * (100 - floor) / 100) + floor;
        var sceneChange = (95 * (100 - floor) / 100) + floor;
        return (minimum, sceneChange);
    }
}
