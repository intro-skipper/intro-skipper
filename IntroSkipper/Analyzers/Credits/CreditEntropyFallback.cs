// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Detects non-black end credits from per-keyframe visual statistics when the black-frame scan
/// finds nothing. Credits rendered on a near-uniform card (text on black, colour, or white) show a
/// sustained low-entropy background that busy content and dark non-credit scenes never produce.
/// </summary>
internal static class CreditEntropyFallback
{
    /// <summary>
    /// Finds the latest sustained low-entropy credit-card run that satisfies the minimum duration.
    /// </summary>
    /// <param name="visuals">The per-keyframe visual statistics, ordered by time.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <returns>The credit time range relative to the credits fingerprint start, or <see langword="null" /> when no run qualifies.</returns>
    public static TimeRange? FindCreditRange(IReadOnlyList<KeyframeVisual> visuals, int minimumDuration)
    {
        if (visuals.Count == 0)
        {
            return null;
        }

        var maximumInRunGap = EstimateMaximumInRunGap(visuals);
        TimeRange? best = null;
        KeyframeVisual? runStart = null;
        KeyframeVisual? lastCard = null;

        foreach (var visual in visuals)
        {
            if (!IsCreditCardKeyframe(visual))
            {
                continue;
            }

            if (runStart is null || lastCard is null)
            {
                runStart = visual;
                lastCard = visual;
                continue;
            }

            if (visual.Time - lastCard.Time > maximumInRunGap)
            {
                best = SelectLongestQualifyingRun(best, runStart, lastCard, minimumDuration);
                runStart = visual;
            }

            lastCard = visual;
        }

        return SelectLongestQualifyingRun(best, runStart, lastCard, minimumDuration);
    }

    /// <summary>
    /// Determines whether a keyframe looks like a near-uniform credit card.
    /// </summary>
    /// <param name="visual">The keyframe visual statistics.</param>
    /// <returns><see langword="true" /> when the keyframe is low entropy and not fully saturated; otherwise, <see langword="false" />.</returns>
    public static bool IsCreditCardKeyframe(KeyframeVisual visual)
        => visual.Entropy < CreditDetectionPolicy.EntropyCreditMaximum &&
           visual.Saturation < CreditDetectionPolicy.SaturationCreditMaximum;

    private static TimeRange? SelectLongestQualifyingRun(
        TimeRange? current,
        KeyframeVisual? runStart,
        KeyframeVisual? lastCard,
        int minimumDuration)
    {
        if (runStart is null || lastCard is null || lastCard.Time - runStart.Time < minimumDuration)
        {
            return current;
        }

        // Credits sit at the tail, so a later qualifying run always supersedes an earlier one.
        return new TimeRange(runStart.Time, lastCard.Time);
    }

    private static double EstimateMaximumInRunGap(IReadOnlyList<KeyframeVisual> visuals)
    {
        if (visuals.Count < 2)
        {
            return CreditDetectionPolicy.MaximumSceneMergeGapSeconds;
        }

        var gaps = new List<double>(visuals.Count - 1);
        for (var i = 1; i < visuals.Count; i++)
        {
            var gap = visuals[i].Time - visuals[i - 1].Time;
            if (gap > 0)
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count == 0)
        {
            return CreditDetectionPolicy.MaximumSceneMergeGapSeconds;
        }

        gaps.Sort();
        return Math.Min(
            CreditDetectionPolicy.MaximumSceneMergeGapSeconds,
            gaps[gaps.Count / 2] * CreditDetectionPolicy.MaximumKeyframeGapMultiplier);
    }
}
