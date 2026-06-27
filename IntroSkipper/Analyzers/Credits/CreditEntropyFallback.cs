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
    private const double IsolatedCardTrimGapMultiplier = 2.5;
    private const double EntropyCreditMaximum = 0.35;
    private const double SaturationCreditMaximum = 96.0;

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
        var runCards = new List<KeyframeVisual>();

        foreach (var visual in visuals)
        {
            if (!IsCreditCardKeyframe(visual))
            {
                continue;
            }

            if (runCards.Count > 0 && visual.Time - runCards[^1].Time > maximumInRunGap)
            {
                best = SelectLatestQualifyingRun(best, runCards, minimumDuration);
                runCards.Clear();
            }

            runCards.Add(visual);
        }

        return SelectLatestQualifyingRun(best, runCards, minimumDuration);
    }

    private static bool IsCreditCardKeyframe(KeyframeVisual visual)
        => visual.Entropy < EntropyCreditMaximum &&
           visual.Saturation < SaturationCreditMaximum;

    private static TimeRange? SelectLatestQualifyingRun(
        TimeRange? currentBest,
        List<KeyframeVisual> runCards,
        int minimumDuration)
    {
        if (runCards.Count == 0)
        {
            return currentBest;
        }

        var (start, end) = TrimIsolatedEnds(runCards);
        var runStart = runCards[start];
        var lastCard = runCards[end];
        if (lastCard.Time - runStart.Time < minimumDuration)
        {
            return currentBest;
        }

        return new TimeRange(runStart.Time, lastCard.Time);
    }

    // Trim isolated edge cards by the run's own cadence; uniformly sparse runs stay intact.
    private static (int Start, int End) TrimIsolatedEnds(List<KeyframeVisual> runCards)
    {
        var start = 0;
        var end = runCards.Count - 1;
        if (end < 1)
        {
            return (start, end);
        }

        var trimGap = MedianCardGap(runCards) * IsolatedCardTrimGapMultiplier;

        while (start < end && runCards[start + 1].Time - runCards[start].Time > trimGap)
        {
            start++;
        }

        while (end > start && runCards[end].Time - runCards[end - 1].Time > trimGap)
        {
            end--;
        }

        return (start, end);
    }

    private static double MedianCardGap(List<KeyframeVisual> runCards)
    {
        var gaps = new List<double>(runCards.Count - 1);
        for (var i = 1; i < runCards.Count; i++)
        {
            gaps.Add(runCards[i].Time - runCards[i - 1].Time);
        }

        gaps.Sort();
        return gaps[gaps.Count / 2];
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
