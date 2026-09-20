// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Finds the card run in the keyframe visuals of one episode: credits on a near-uniform
/// low-saturation card. Text on black, white, grey or a muted colour card shows a near-uniform
/// background with text on it, which busy content and flat backgrounds with a subject in front
/// never produce. Inside a black scene the black-frame rules accepted, a black keyframe whatever it
/// shows and a card-like keyframe are both black cards: they extend a run and count toward its
/// duration but never toward its density or cadence, so a black roll cannot carry stray flat shots
/// before it into the credits or trim sparse white cards next to it. A black card-like keyframe
/// outside every accepted scene is content.
/// </summary>
internal static class CardRunFinder
{
    private const double IsolatedCardTrimGapMultiplier = 2.5;

    // A card's background holds at least 80 percent of the pixels within a few luma levels.
    private const double BackgroundSpreadMaximum = 8;

    // Something is drawn on the background, text usually, at least this far from it in luma. A fade
    // or a bare wall has the spread but not the contrast.
    private const double TextContrastMinimum = 60;

    // Vivid/saturated uniform frames are excluded on purpose: a solid-colour content frame (a fade,
    // stylised transition, or saturated sky) is indistinguishable from a saturated colour card by
    // spread and saturation alone, so admitting them would cost the card run finder's zero-false-positive
    // discipline. Cards are therefore muted/neutral (low saturation), not vivid colour.
    private const double SaturationCreditMaximum = 96.0;
    private const double MinimumCardFraction = 0.5;

    // The blackframe and metadata filters format the same pts differently, so the same keyframe
    // can sit under a millisecond apart in the two lists.
    private const double BlackKeyframeJoinTolerance = 0.01;

    private enum KeyframeKind
    {
        Content,
        Card,
        BlackCard,
    }

    /// <summary>
    /// Finds the latest sustained run of card keyframes that satisfies the minimum duration.
    /// </summary>
    /// <remarks>
    /// The black scenes the black-frame rules accepted, which carry their interval and boundary
    /// evidence, are the only black evidence used here. One of them is the black-frame candidate; the
    /// others still count, since a card run next to a scene the rules did not pick is its own credits.
    /// A black keyframe inside an accepted scene is a black card whatever it shows, so a blank page
    /// between two roll pages does not break the roll, and so is a card-like keyframe inside one, so a
    /// vanity card between two roll parts cannot give the roll a card density of its own: both extend
    /// the run and count toward its duration, so short white cards and a short roll qualify together,
    /// but they are left out of the density ratio and the trim cadence. Counted, a black roll's density carried scattered
    /// flat shots before it into the run: measured on an anime epilogue, that admitted 67 seconds of
    /// story. A black card-like keyframe outside every accepted scene is content, since the black-frame
    /// rules rejected it, as it does a dark lead-in before the roll's transition or a black flash
    /// before an interval-confirmed roll. With no candidate the visuals alone decide, as the old
    /// fallback did, so black cards the black-frame rules could not confirm still count.
    /// </remarks>
    /// <param name="visuals">The per-keyframe visual statistics, ordered by time.</param>
    /// <param name="blackFrames">The black-frame scan over the same keyframes, ordered by time.</param>
    /// <param name="blackMinimum">The black percentage at or above which a keyframe is black, normalized against the scan by the black-frame rules.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <param name="blackFrameScenes">The black scenes the black-frame rules accepted, relative to the credits fingerprint start; empty when they found no credits.</param>
    /// <returns>The credit time range relative to the credits fingerprint start, or <see langword="null" /> when no run qualifies.</returns>
    public static TimeRange? FindCreditRange(IReadOnlyList<KeyframeVisual> visuals, IReadOnlyList<BlackFrame> blackFrames, int blackMinimum, int minimumDuration, IReadOnlyList<TimeRange> blackFrameScenes)
    {
        if (blackFrameScenes.Count == 0)
        {
            return FindCreditRange(visuals, minimumDuration);
        }

        List<double> blackTimes = [.. blackFrames.Where(frame => frame.Percentage >= blackMinimum).Select(frame => frame.Time)];
        return FindCreditRange(Classify(visuals, blackTimes, blackFrameScenes), minimumDuration);
    }

    /// <summary>
    /// Finds the latest sustained run of card keyframes that satisfies the minimum duration, with no
    /// black-frame scan to tell black cards from other cards.
    /// </summary>
    /// <param name="visuals">The per-keyframe visual statistics, ordered by time.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <returns>The credit time range relative to the credits fingerprint start, or <see langword="null" /> when no run qualifies.</returns>
    public static TimeRange? FindCreditRange(IReadOnlyList<KeyframeVisual> visuals, int minimumDuration)
        => FindCreditRange(Classify(visuals, [], []), minimumDuration);

    /// <summary>
    /// Classifies a keyframe as a credit card: a dominant near-uniform background, something drawn on
    /// it far from the background in luma, and low saturation (not a vivid colour scene). Exposed as
    /// <see langword="internal" /> so the classification boundary can be unit-tested directly.
    /// </summary>
    /// <param name="visual">The per-keyframe visual statistics.</param>
    /// <returns><see langword="true" /> when the keyframe looks like a credit card.</returns>
    internal static bool IsCreditCardKeyframe(KeyframeVisual visual)
        => visual.LumaHigh - visual.LumaLow <= BackgroundSpreadMaximum &&
           Math.Max(visual.LumaMax - visual.LumaHigh, visual.LumaLow - visual.LumaMin) >= TextContrastMinimum &&
           visual.Saturation < SaturationCreditMaximum;

    private static List<CardKeyframe> Classify(IReadOnlyList<KeyframeVisual> visuals, List<double> blackTimes, IReadOnlyList<TimeRange> blackFrameScenes)
    {
        var keyframes = new List<CardKeyframe>(visuals.Count);
        var next = 0;
        foreach (var visual in visuals)
        {
            while (next < blackTimes.Count && blackTimes[next] < visual.Time - BlackKeyframeJoinTolerance)
            {
                next++;
            }

            var black = next < blackTimes.Count && blackTimes[next] - visual.Time <= BlackKeyframeJoinTolerance;
            var card = IsCreditCardKeyframe(visual);
            var kind = (black || card) && blackFrameScenes.Any(scene => visual.Time >= scene.Start && visual.Time <= scene.End) ? KeyframeKind.BlackCard
                : card && !black ? KeyframeKind.Card
                : KeyframeKind.Content;
            keyframes.Add(new CardKeyframe(visual.Time, kind));
        }

        return keyframes;
    }

    private static TimeRange? FindCreditRange(List<CardKeyframe> keyframes, int minimumDuration)
    {
        if (keyframes.Count == 0)
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
        var runCards = new List<CardKeyframe>();
        var nonCardSinceLastCard = false;

        foreach (var keyframe in keyframes)
        {
            if (keyframe.Kind == KeyframeKind.Content)
            {
                nonCardSinceLastCard = true;
                continue;
            }

            if (runCards.Count > 0 &&
                keyframe.Time - runCards[^1].Time > maximumInRunGap &&
                nonCardSinceLastCard)
            {
                best = SelectLatestQualifyingRun(best, runCards, keyframes, minimumDuration);
                runCards.Clear();
            }

            runCards.Add(keyframe);
            nonCardSinceLastCard = false;
        }

        return SelectLatestQualifyingRun(best, runCards, keyframes, minimumDuration);
    }

    private static TimeRange? SelectLatestQualifyingRun(
        TimeRange? currentBest,
        List<CardKeyframe> runCards,
        List<CardKeyframe> keyframes,
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
        if (!HasSufficientCardDensity(keyframes, runStart.Time, lastCard.Time))
        {
            return currentBest;
        }

        return new TimeRange(runStart.Time, lastCard.Time);
    }

    // Fraction of non-black keyframes inside the run's span that look like credit cards. This is the
    // only place intervening busy content (skipped during grouping) re-enters the qualification
    // decision. Black cards are neither cards nor content here, so a run that is a roll and nothing
    // else has no density and is left to the black-frame candidate.
    private static bool HasSufficientCardDensity(List<CardKeyframe> keyframes, double startTime, double endTime)
    {
        var total = 0;
        var cards = 0;
        foreach (var keyframe in keyframes)
        {
            if (keyframe.Time < startTime || keyframe.Time > endTime || keyframe.Kind == KeyframeKind.BlackCard)
            {
                continue;
            }

            total++;
            if (keyframe.Kind == KeyframeKind.Card)
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
    private static (int Start, int End) TrimIsolatedEnds(List<CardKeyframe> runCards, int minimumDuration)
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
    // that would pull the median up and stop the trim from removing isolated edge cards. Measured over
    // the non-black cards when there are enough of them, so a dense roll's cadence does not trim
    // sparse white cards next to it.
    private static double DenseCadenceGap(List<CardKeyframe> runCards)
    {
        var cards = runCards.Where(card => card.Kind == KeyframeKind.Card).ToList();
        if (cards.Count < 2)
        {
            cards = runCards;
        }

        var gaps = new List<double>(cards.Count - 1);
        for (var i = 1; i < cards.Count; i++)
        {
            gaps.Add(cards[i].Time - cards[i - 1].Time);
        }

        gaps.Sort();
        return gaps[gaps.Count / 4];
    }

    private readonly record struct CardKeyframe(double Time, KeyframeKind Kind);
}
