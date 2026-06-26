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
        var trailingTrimGap = maximumInRunGap * CreditDetectionPolicy.TrailingTrimGapFactor;
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
                best = SelectLatestQualifyingRun(best, runCards, minimumDuration, trailingTrimGap);
                runCards.Clear();
            }

            runCards.Add(visual);
        }

        return SelectLatestQualifyingRun(best, runCards, minimumDuration, trailingTrimGap);
    }

    /// <summary>
    /// Determines whether a keyframe looks like a near-uniform credit card.
    /// </summary>
    /// <param name="visual">The keyframe visual statistics.</param>
    /// <returns><see langword="true" /> when the keyframe is low entropy and not fully saturated; otherwise, <see langword="false" />.</returns>
    public static bool IsCreditCardKeyframe(KeyframeVisual visual)
        => visual.Entropy < CreditDetectionPolicy.EntropyCreditMaximum &&
           visual.Saturation < CreditDetectionPolicy.SaturationCreditMaximum;

    /// <summary>
    /// Trims an over-extended tail of isolated cards from <paramref name="runCards" />, then returns
    /// that run when it still meets the minimum duration; otherwise keeps <paramref name="currentBest" />.
    /// </summary>
    /// <remarks>
    /// Credits sit at the tail and form a dense card cluster, so over-extension shows up as trailing
    /// cards attached only through an above-cadence gap (periodic near-uniform frames in non-credit
    /// content). Walking the end back while the final card is farther than <paramref name="trailingTrimGap" />
    /// from its predecessor drops those isolated cards without touching the dense body, so genuinely
    /// interleaved credits (a brief ident bracketed by cards) are preserved. A qualifying later run
    /// still supersedes an earlier one; <paramref name="currentBest" /> is the running best carried
    /// across run boundaries, not compared by length.
    /// </remarks>
    private static TimeRange? SelectLatestQualifyingRun(
        TimeRange? currentBest,
        List<KeyframeVisual> runCards,
        int minimumDuration,
        double trailingTrimGap)
    {
        if (runCards.Count == 0)
        {
            return currentBest;
        }

        var end = runCards.Count - 1;
        while (end > 0 && runCards[end].Time - runCards[end - 1].Time > trailingTrimGap)
        {
            end--;
        }

        var runStart = runCards[0];
        var lastCard = runCards[end];
        if (lastCard.Time - runStart.Time < minimumDuration)
        {
            return currentBest;
        }

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
