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
        ArgumentNullException.ThrowIfNull(visuals);

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

    /// <summary>
    /// Classifies a keyframe as a near-uniform credit card: low luma entropy (uniform background)
    /// and low saturation (not a vivid colour scene). Exposed as <see langword="internal" /> so the
    /// entropy/saturation classification boundary can be unit-tested directly.
    /// </summary>
    /// <param name="visual">The per-keyframe visual statistics.</param>
    /// <returns><see langword="true" /> when the keyframe looks like a uniform credit card.</returns>
    internal static bool IsCreditCardKeyframe(KeyframeVisual visual)
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

        var (start, end) = TrimIsolatedEnds(runCards, minimumDuration);
        var runStart = runCards[start];
        var lastCard = runCards[end];
        if (lastCard.Time - runStart.Time < minimumDuration)
        {
            return currentBest;
        }

        return new TimeRange(runStart.Time, lastCard.Time);
    }

    // Trim sparse isolated cards from each edge of the run, anchored to the dense-body cadence rather
    // than the run's overall median (which a long sparse tail can dominate and thereby block the trim).
    // When trimming leaves less than the minimum duration there is no dominant dense body, so the run
    // only qualifies (if at all) as a uniformly sparse credit run: keep its full span rather than
    // collapsing it to the brief dense edge.
    private static (int Start, int End) TrimIsolatedEnds(List<KeyframeVisual> runCards, int minimumDuration)
    {
        var start = 0;
        var end = runCards.Count - 1;
        if (end < 1)
        {
            return (start, end);
        }

        var trimGap = DenseCadenceGap(runCards) * IsolatedCardTrimGapMultiplier;

        while (start < end && runCards[start + 1].Time - runCards[start].Time > trimGap)
        {
            start++;
        }

        while (end > start && runCards[end].Time - runCards[end - 1].Time > trimGap)
        {
            end--;
        }

        if (runCards[end].Time - runCards[start].Time < minimumDuration)
        {
            return (0, runCards.Count - 1);
        }

        return (start, end);
    }

    // Lower-quartile card-to-card gap: the cadence of the dense body, robust to a sparse tail or head
    // that would pull the median up and stop the trim from removing isolated edge cards.
    private static double DenseCadenceGap(List<KeyframeVisual> runCards)
    {
        var gaps = new List<double>(runCards.Count - 1);
        for (var i = 1; i < runCards.Count; i++)
        {
            gaps.Add(runCards[i].Time - runCards[i - 1].Time);
        }

        gaps.Sort();
        return gaps[gaps.Count / 4];
    }

    // Estimate the credit-card cadence from card keyframes only. Including every keyframe lets dense
    // non-card content before the credits drive the median down, which tightens the grouping gap below
    // the actual card spacing and splits sparse static-card credits into discarded sub-minimum runs.
    private static double EstimateMaximumInRunGap(IReadOnlyList<KeyframeVisual> visuals)
    {
        var gaps = CardGaps(visuals);
        if (gaps.Count == 0)
        {
            return CreditDetectionPolicy.MaximumSceneMergeGapSeconds;
        }

        gaps.Sort();
        return Math.Min(
            CreditDetectionPolicy.MaximumSceneMergeGapSeconds,
            gaps[gaps.Count / 2] * CreditDetectionPolicy.MaximumKeyframeGapMultiplier);
    }

    private static List<double> CardGaps(IReadOnlyList<KeyframeVisual> visuals)
    {
        var gaps = new List<double>();
        KeyframeVisual? previousCard = null;
        foreach (var visual in visuals)
        {
            if (!IsCreditCardKeyframe(visual))
            {
                continue;
            }

            if (previousCard is not null)
            {
                var gap = visual.Time - previousCard.Time;
                if (gap > 0)
                {
                    gaps.Add(gap);
                }
            }

            previousCard = visual;
        }

        return gaps;
    }
}
