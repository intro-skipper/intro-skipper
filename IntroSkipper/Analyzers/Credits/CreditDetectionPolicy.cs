// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Defines credit-detection policy constants and derived thresholds.
/// </summary>
internal static class CreditDetectionPolicy
{
    public const double MaximumSceneMergeGapSeconds = 20;
    public const double MaximumKeyframeGapMultiplier = 5.0;
    public const double DefaultMinimumBlackFrameDensity = 0.50;
    public const double MaximumIntervalToKeyframeGapSeconds = 2.0;

    private const double SparseAverageBlackFrameGapFactor = 0.5;
    private const double IntervalProbePaddingFactor = 1.0;
    private const int MinimumAdaptiveDensitySampleCount = 3;
    private const double AdaptiveDensityMedianScale = 0.60;

    /// <summary>
    /// Computes the minimum black-frame density required for the current candidate distribution.
    /// </summary>
    /// <remarks>
    /// Uses the default density when fewer than three measured scenes are available; otherwise, scales
    /// the median candidate density and caps it at the default to relax detection only when repeated
    /// low-density credit evidence exists.
    /// </remarks>
    /// <param name="metrics">The measured scene metrics.</param>
    /// <returns>The minimum density required for a scene to remain eligible.</returns>
    public static double ComputeMinimumBlackFrameDensity(IReadOnlyList<CreditSceneMetrics> metrics)
    {
        var validDensities = metrics
            .Where(metric => metric.TotalFrameCount > 0)
            .Select(metric => metric.BlackFrameDensity)
            .OrderBy(density => density)
            .ToList();

        if (validDensities.Count < MinimumAdaptiveDensitySampleCount)
        {
            return DefaultMinimumBlackFrameDensity;
        }

        var middle = validDensities.Count / 2;
        var median = validDensities.Count % 2 == 0
            ? (validDensities[middle - 1] + validDensities[middle]) / 2d
            : validDensities[middle];

        return Math.Min(DefaultMinimumBlackFrameDensity, median * AdaptiveDensityMedianScale);
    }

    public static double MaximumSparseAverageBlackFrameGap(int minimumDuration)
        => minimumDuration * SparseAverageBlackFrameGapFactor;

    public static double IntervalProbePadding(int minimumDuration)
        => minimumDuration * IntervalProbePaddingFactor;
}
