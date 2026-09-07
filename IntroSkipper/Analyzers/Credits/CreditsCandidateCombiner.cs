// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Combines the credits candidates of an episode into the segments to store.
/// </summary>
/// <remarks>
/// A candidate that ends within the minimum credits duration of the window end is extended
/// to it: nothing that short after credits can be a credits scene of its own, and shared
/// audio stops a few seconds early at the fade-out. Candidates that then overlap or lie
/// within <see cref="CreditDetectionPolicy.MaximumSceneMergeGapSeconds"/> of each other
/// merge into one segment from the earliest start to the latest end under
/// <see cref="SegmentSource.Combined"/>. Candidates farther apart are emitted separately
/// under their own source. Order carries no information: shared audio before,
/// inside or after a black run merges the same way, and a union only grows, so a chapter
/// range is never shortened.
/// </remarks>
internal static class CreditsCandidateCombiner
{
    /// <summary>
    /// Combines the candidates of one episode.
    /// </summary>
    /// <param name="candidates">The candidates, in any order, each already bounded by its own analyzer. Invalid segments are ignored.</param>
    /// <param name="windowEnd">The end of the credits window in seconds.</param>
    /// <param name="minimumDuration">The minimum credits duration in seconds.</param>
    /// <returns>The segments to store, ordered by start; empty when no candidate is valid.</returns>
    public static List<AttributedSegment> Combine(IReadOnlyList<AttributedSegment> candidates, double windowEnd, int minimumDuration)
    {
        List<AttributedSegment> ordered = [.. candidates
            .Where(c => c.Segment.Valid)
            .Select(c => ReachesWindowEnd(c.Segment.End, windowEnd, minimumDuration) && c.Segment.End < windowEnd
                ? new AttributedSegment(new Segment(c.Segment.EpisodeId, new TimeRange(c.Segment.Start, windowEnd)), c.Source)
                : c)
            .OrderBy(c => c.Segment.Start)];

        var result = new List<AttributedSegment>(ordered.Count);
        foreach (var candidate in ordered)
        {
            if (result.Count > 0 &&
                candidate.Segment.Start <= result[^1].Segment.End + CreditDetectionPolicy.MaximumSceneMergeGapSeconds)
            {
                var current = result[^1];
                result[^1] = new AttributedSegment(
                    new Segment(current.Segment.EpisodeId, new TimeRange(current.Segment.Start, Math.Max(current.Segment.End, candidate.Segment.End))),
                    SegmentSource.Combined);
                continue;
            }

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// Whether a candidate ends so close to the window end that no credits scene fits after it.
    /// </summary>
    /// <param name="candidateEnd">The candidate's end in seconds.</param>
    /// <param name="windowEnd">The end of the credits window in seconds.</param>
    /// <param name="minimumDuration">The minimum credits duration in seconds.</param>
    /// <returns><see langword="true"/> if the candidate reaches the window end for combination purposes; otherwise, <see langword="false"/>.</returns>
    public static bool ReachesWindowEnd(double candidateEnd, double windowEnd, int minimumDuration)
        => candidateEnd > windowEnd - minimumDuration;
}
