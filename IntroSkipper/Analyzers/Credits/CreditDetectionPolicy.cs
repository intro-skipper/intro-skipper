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

    /// <summary>
    /// Fraction of the in-run bridge gap within which a trailing card must sit to be kept. Credits
    /// form a dense card cluster at the tail, so a trailing card farther than this from its
    /// predecessor is treated as an over-extended tail (periodic near-uniform frames in non-credit
    /// content) and trimmed from the credit run end, without touching the dense body.
    /// </summary>
    public const double TrailingTrimGapFactor = 0.5;
    public const double DefaultMinimumBlackFrameDensity = 0.50;
    public const double MaximumIntervalToKeyframeGapSeconds = 2.0;

    // Minimum overlap between a candidate scene and a blackdetect interval to count as interval support.
    public const double MinimumIntervalOverlapSeconds = 0.25;

    // Minimum keyframe gap before a scene start for boundary probing to be worthwhile.
    public const double MinimumBoundaryProbeWindow = 0.50;

    /// <summary>
    /// Maximum normalised luma entropy for a keyframe to count as a near-uniform credit "card".
    /// Busy content and dark non-credit scenes sit well above this (~0.5+); text on a solid
    /// black/coloured/bright card sits below it.
    /// </summary>
    public const double EntropyCreditMaximum = 0.35;

    /// <summary>
    /// Maximum mean saturation (<c>SATAVG</c>) for a credit-card keyframe. A generous ceiling that
    /// admits muted cards (greyscale, navy, slate) while rejecting fully saturated content.
    /// </summary>
    public const double SaturationCreditMaximum = 96.0;

    private const double SparseAverageBlackFrameGapFactor = 0.5;
    private const double IntervalProbePaddingFactor = 1.0;

    public static double MaximumSparseAverageBlackFrameGap(int minimumDuration)
        => minimumDuration * SparseAverageBlackFrameGapFactor;

    public static double IntervalProbePadding(int minimumDuration)
        => minimumDuration * IntervalProbePaddingFactor;
}
