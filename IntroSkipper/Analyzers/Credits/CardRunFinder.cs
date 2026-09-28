// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Finds the card run in one episode's pages, each read as its time and card kind: credits on a
/// near-uniform low-saturation card. A black card extends a run and counts toward its duration but
/// never toward its density, and the trim cadence reads black cards only when a run has fewer than
/// two cards, so a black roll cannot carry stray flat shots before it into the credits or trim sparse
/// white cards next to it. The keyframe analyzer sets the kinds (see
/// <see cref="KeyframeAnalyzer.StampCardKinds"/>).
/// </summary>
internal static class CardRunFinder
{
    private const double IsolatedCardTrimGapMultiplier = 2.5;
    private const double MinimumCardFraction = 0.5;

    /// <summary>
    /// Finds the latest sustained run of card pages that satisfies the minimum duration.
    /// </summary>
    /// <param name="pages">The pages that have a visual, ordered by time, each with its card kind. A run is timed on the pages' black-frame times.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <returns>The credit time range relative to the credits fingerprint start, or <see langword="null" /> when no run qualifies.</returns>
    public static TimeRange? FindCreditRange(IReadOnlyList<CardPage> pages, int minimumDuration)
    {
        if (pages.Count == 0)
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
        var runCards = new List<CardPage>();
        var nonCardSinceLastCard = false;

        foreach (var page in pages)
        {
            if (page.Kind == CardKind.Content)
            {
                nonCardSinceLastCard = true;
                continue;
            }

            if (runCards.Count > 0 &&
                page.Time - runCards[^1].Time > maximumInRunGap &&
                nonCardSinceLastCard)
            {
                best = SelectLatestQualifyingRun(best, runCards, pages, minimumDuration);
                runCards.Clear();
            }

            runCards.Add(page);
            nonCardSinceLastCard = false;
        }

        return SelectLatestQualifyingRun(best, runCards, pages, minimumDuration);
    }

    private static TimeRange? SelectLatestQualifyingRun(
        TimeRange? currentBest,
        List<CardPage> runCards,
        IReadOnlyList<CardPage> pages,
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
        if (!HasSufficientCardDensity(pages, runStart.Time, lastCard.Time))
        {
            return currentBest;
        }

        return new TimeRange(runStart.Time, lastCard.Time);
    }

    // Fraction of non-black keyframes inside the run's span that look like credit cards. This is the
    // only place intervening busy content (skipped during grouping) re-enters the qualification
    // decision. Black cards are neither cards nor content here, so a run that is a roll and nothing
    // else has no density and is left to the black-frame candidate.
    private static bool HasSufficientCardDensity(IReadOnlyList<CardPage> pages, double startTime, double endTime)
    {
        var total = 0;
        var cards = 0;
        foreach (var page in pages)
        {
            if (page.Time < startTime || page.Time > endTime || page.Kind == CardKind.BlackCard)
            {
                continue;
            }

            total++;
            if (page.Kind == CardKind.Card)
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
    private static (int Start, int End) TrimIsolatedEnds(List<CardPage> runCards, int minimumDuration)
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
    private static double DenseCadenceGap(List<CardPage> runCards)
    {
        var cards = runCards.Where(card => card.Kind == CardKind.Card).ToList();
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
}
