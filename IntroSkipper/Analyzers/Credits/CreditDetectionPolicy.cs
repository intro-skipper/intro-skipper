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

    public static double MaximumSparseAverageBlackFrameGap(int minimumDuration)
        => minimumDuration * SparseAverageBlackFrameGapFactor;

    public static double IntervalProbePadding(int minimumDuration)
        => minimumDuration * IntervalProbePaddingFactor;
}
