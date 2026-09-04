// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Shared black-frame threshold normalization helpers.
/// </summary>
internal static class BlackFrameThresholdHelper
{
    // Caps the darkness floor so one very dark scan cannot push the thresholds past usefulness.
    private const int MaximumFloor = 30;

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

        // The floor is the 1st-percentile black percentage, capped at MaximumFloor. Percentages at
        // or above the cap all map to the cap, so a histogram of MaximumFloor + 1 buckets replaces
        // sorting the whole scan.
        var counts = new int[MaximumFloor + 1];
        foreach (var frame in frames)
        {
            counts[Math.Clamp(frame.Percentage, 0, MaximumFloor)]++;
        }

        // Clamp into range: for short/sparse scans, frames.Count * 0.01 floors to 0,
        // so the floor becomes the single least-black frame.
        var remaining = Math.Clamp((int)(frames.Count * 0.01), 0, frames.Count - 1);
        var floor = 0;
        while (remaining >= counts[floor])
        {
            remaining -= counts[floor];
            floor++;
        }

        var minimum = (minimumPercentage * (100 - floor) / 100) + floor;
        var sceneChange = (95 * (100 - floor) / 100) + floor;
        return (minimum, sceneChange);
    }
}
