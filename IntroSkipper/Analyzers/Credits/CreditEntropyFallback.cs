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

    // Vivid/saturated uniform frames are excluded on purpose: a solid-colour content frame (a fade,
    // stylised transition, or saturated sky) is indistinguishable from a saturated colour card by
    // entropy + saturation alone, so admitting them would cost the fallback's zero-false-positive
    // discipline. Cards are therefore muted/neutral (low saturation), not vivid colour.
    private const double SaturationCreditMaximum = 96.0;
    private const double MinimumCardFraction = 0.5;

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

        // A run breaks only when real content separates two cards: a time gap beyond the fixed bridge
        // AND at least one non-card keyframe between them. The fixed bridge (not a cadence estimate)
        // keeps runs independent of an earlier dense run, while the non-card-evidence requirement keeps
        // a sparse all-card source (keyframes farther apart than the bridge but with nothing non-card
        // between) as a single run regardless of GOP length.
        const double maximumInRunGap = CreditDetectionPolicy.MaximumSceneMergeGapSeconds;
        TimeRange? best = null;
        var runCards = new List<KeyframeVisual>();
        var nonCardSinceLastCard = false;

        foreach (var visual in visuals)
        {
            if (!IsCreditCardKeyframe(visual))
            {
                nonCardSinceLastCard = true;
                continue;
            }

            if (runCards.Count > 0 &&
                visual.Time - runCards[^1].Time > maximumInRunGap &&
                nonCardSinceLastCard)
            {
                best = SelectLatestQualifyingRun(best, runCards, visuals, minimumDuration);
                runCards.Clear();
            }

            runCards.Add(visual);
            nonCardSinceLastCard = false;
        }

        return SelectLatestQualifyingRun(best, runCards, visuals, minimumDuration);
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
        IReadOnlyList<KeyframeVisual> visuals,
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

        // Cards must dominate the keyframes within the span. The grouping step skips non-card
        // keyframes, so two isolated cards bridged across busy content (non-card keyframes between
        // them) would otherwise masquerade as a sustained card sequence. A genuinely sparse credit
        // run from a long-GOP source has no non-card keyframes between its cards, so its density
        // stays high and it is kept; only sparse cards interspersed with busy content are rejected.
        if (!HasSufficientCardDensity(visuals, runStart.Time, lastCard.Time))
        {
            return currentBest;
        }

        return new TimeRange(runStart.Time, lastCard.Time);
    }

    // Fraction of keyframes inside the run's span that look like credit cards. This is the only place
    // intervening busy content (skipped during grouping) re-enters the qualification decision.
    private static bool HasSufficientCardDensity(IReadOnlyList<KeyframeVisual> visuals, double startTime, double endTime)
    {
        var total = 0;
        var cards = 0;
        foreach (var visual in visuals)
        {
            if (visual.Time < startTime || visual.Time > endTime)
            {
                continue;
            }

            total++;
            if (IsCreditCardKeyframe(visual))
            {
                cards++;
            }
        }

        return total > 0 && (double)cards / total >= MinimumCardFraction;
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
}
