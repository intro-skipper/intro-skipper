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
/// under their own source. Order carries no information: shared audio before, inside or
/// after a black run merges the same way, and a union only grows, so a chapter range is
/// never shortened.
/// </remarks>
internal static class CreditsCandidateCombiner
{
    // Chapter starts are rounded to ticks; half a second absorbs that and keyframe jitter.
    private const double BoundaryTolerance = 0.5;

    /// <summary>
    /// Combines the candidates of one episode.
    /// </summary>
    /// <remarks>
    /// A chapter boundary that follows a chapter credits candidate is an authored end of the
    /// credits, such as a named preview chapter. No candidate extends or merges across it,
    /// and a black-frame or chromaprint candidate reaching past it is capped there. Chapter
    /// boundaries carry no such meaning when the chapter analyzer found no credits chapter.
    /// </remarks>
    /// <param name="candidates">The candidates, in any order, each already bounded by its own analyzer. Invalid segments are ignored.</param>
    /// <param name="windowEnd">The end of the credits window in seconds.</param>
    /// <param name="minimumDuration">The minimum credits duration in seconds.</param>
    /// <param name="chapterStarts">The episode's chapter start times in seconds.</param>
    /// <returns>The segments to store, ordered by start; empty when no candidate is valid.</returns>
    public static List<AttributedSegment> Combine(
        IReadOnlyList<AttributedSegment> candidates,
        double windowEnd,
        int minimumDuration,
        IReadOnlyList<double> chapterStarts)
    {
        var boundaries = HardBoundaries(candidates, windowEnd, chapterStarts);

        List<AttributedSegment> ordered = [.. candidates
            .Where(c => c.Segment.Valid)
            .Select(c => Bound(c, boundaries, windowEnd, minimumDuration))
            .OfType<AttributedSegment>()
            .OrderBy(c => c.Segment.Start)];

        var result = new List<AttributedSegment>(ordered.Count);
        foreach (var candidate in ordered)
        {
            if (result.Count > 0 &&
                candidate.Segment.Start <= result[^1].Segment.End + CreditDetectionPolicy.MaximumSceneMergeGapSeconds &&
                !boundaries.Any(b => b >= result[^1].Segment.End - BoundaryTolerance && b <= candidate.Segment.Start + BoundaryTolerance))
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

    private static List<double> HardBoundaries(IReadOnlyList<AttributedSegment> candidates, double windowEnd, IReadOnlyList<double> chapterStarts)
    {
        List<Segment> chapters = [.. candidates.Where(c => c.Source == SegmentSource.Chapter && c.Segment.Valid).Select(c => c.Segment)];
        if (chapters.Count == 0)
        {
            return [];
        }

        return [.. chapterStarts
            .Where(b => b < windowEnd - BoundaryTolerance
                && chapters.Any(c => b >= c.End - BoundaryTolerance)
                && !chapters.Any(c => b >= c.Start - BoundaryTolerance && b < c.End - BoundaryTolerance))
            .OrderBy(b => b)];
    }

    /// <summary>
    /// Caps a non-chapter candidate at the first hard boundary after its start and extends any
    /// candidate that reaches the window end, unless a hard boundary stands in the way.
    /// </summary>
    /// <returns>The bounded candidate, or <see langword="null"/> when capping leaves too little of it.</returns>
    private static AttributedSegment? Bound(AttributedSegment candidate, IReadOnlyList<double> boundaries, double windowEnd, int minimumDuration)
    {
        var start = candidate.Segment.Start;
        var end = candidate.Segment.End;

        if (candidate.Source != SegmentSource.Chapter)
        {
            var cap = boundaries.FirstOrDefault(b => b > start + BoundaryTolerance, double.NaN);
            if (!double.IsNaN(cap) && end > cap)
            {
                end = cap;
                if (end - start < minimumDuration)
                {
                    return null;
                }
            }
        }

        if (ReachesWindowEnd(end, windowEnd, minimumDuration) && end < windowEnd &&
            !boundaries.Any(b => b >= end - BoundaryTolerance))
        {
            end = windowEnd;
        }

        return end == candidate.Segment.End
            ? candidate
            : new AttributedSegment(new Segment(candidate.Segment.EpisodeId, new TimeRange(start, end)), candidate.Source);
    }
}
