// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// Pure geometry for comparing a detected recap interval against a ground-truth interval.
/// Every method is deterministic and side-effect free, so it can be unit tested without media.
/// </summary>
internal static class RecapMetrics
{
    /// <summary>
    /// Returns the overlap, in seconds, shared by both intervals (never negative).
    /// Returns 0 when either interval is empty or they do not overlap.
    /// </summary>
    /// <param name="a">First interval.</param>
    /// <param name="b">Second interval.</param>
    /// <returns>The intersection length, in seconds.</returns>
    public static double Intersection(RecapInterval a, RecapInterval b)
    {
        if (!a.HasValue || !b.HasValue)
        {
            return 0.0;
        }

        var start = Math.Max(a.Start, b.Start);
        var end = Math.Min(a.End, b.End);
        return Math.Max(0.0, end - start);
    }

    /// <summary>
    /// Returns the union, in seconds, covered by either interval.
    /// Returns 0 when both intervals are empty.
    /// </summary>
    /// <param name="a">First interval.</param>
    /// <param name="b">Second interval.</param>
    /// <returns>The union length, in seconds.</returns>
    public static double Union(RecapInterval a, RecapInterval b)
    {
        var aDuration = a.HasValue ? a.Duration : 0.0;
        var bDuration = b.HasValue ? b.Duration : 0.0;
        return aDuration + bDuration - Intersection(a, b);
    }

    /// <summary>
    /// Returns the Intersection-over-Union (Jaccard overlap) of the two intervals, in the range [0, 1].
    /// Returns 0 when the union is 0 (e.g. both intervals empty), so a miss scores 0 rather than NaN.
    /// </summary>
    /// <param name="a">First interval.</param>
    /// <param name="b">Second interval.</param>
    /// <returns>The IoU, in the range [0, 1].</returns>
    public static double IntersectionOverUnion(RecapInterval a, RecapInterval b)
    {
        var union = Union(a, b);
        if (union <= 0.0)
        {
            return 0.0;
        }

        return Intersection(a, b) / union;
    }

    /// <summary>
    /// Returns the absolute error, in seconds, between the detected and truth start boundaries.
    /// </summary>
    /// <param name="detected">Detected interval.</param>
    /// <param name="truth">Ground-truth interval.</param>
    /// <returns>The absolute start error, in seconds.</returns>
    public static double AbsoluteStartError(RecapInterval detected, RecapInterval truth)
        => Math.Abs(detected.Start - truth.Start);

    /// <summary>
    /// Returns the absolute error, in seconds, between the detected and truth end boundaries.
    /// </summary>
    /// <param name="detected">Detected interval.</param>
    /// <param name="truth">Ground-truth interval.</param>
    /// <returns>The absolute end error, in seconds.</returns>
    public static double AbsoluteEndError(RecapInterval detected, RecapInterval truth)
        => Math.Abs(detected.End - truth.End);

    /// <summary>
    /// Returns the seconds of the detected interval that fall OUTSIDE the truth interval — i.e.
    /// non-recap content (cold open / episode body) the user would wrongly skip. This is the
    /// HARMFUL over-reach direction: e.g. a recap whose start is forced to 0 swallows the cold open.
    /// Returns 0 when nothing was detected. On a no-recap truth this equals the whole detection.
    /// </summary>
    /// <param name="detected">Detected interval.</param>
    /// <param name="truth">Ground-truth interval.</param>
    /// <returns>Seconds of non-recap content inside the detection.</returns>
    public static double ContentOutsideTruth(RecapInterval detected, RecapInterval truth)
    {
        if (!detected.HasValue)
        {
            return 0.0;
        }

        return Math.Max(0.0, detected.Duration - Intersection(detected, truth));
    }

    /// <summary>
    /// Returns the seconds of the truth interval NOT covered by the detection — recap the user still
    /// sees. This is the MILDER under-reach direction (annoyance, not lost story). Returns 0 when
    /// there is no truth interval.
    /// </summary>
    /// <param name="detected">Detected interval.</param>
    /// <param name="truth">Ground-truth interval.</param>
    /// <returns>Seconds of the true recap left uncovered.</returns>
    public static double TruthNotCovered(RecapInterval detected, RecapInterval truth)
    {
        if (!truth.HasValue)
        {
            return 0.0;
        }

        return Math.Max(0.0, truth.Duration - Intersection(detected, truth));
    }

    /// <summary>
    /// Returns whether the detected interval is considered a correct localization of the truth
    /// interval, i.e. their IoU meets or exceeds <paramref name="iouMatchThreshold"/>.
    /// </summary>
    /// <param name="detected">Detected interval.</param>
    /// <param name="truth">Ground-truth interval.</param>
    /// <param name="iouMatchThreshold">Minimum IoU required to count as a match, in the range [0, 1].</param>
    /// <returns><see langword="true"/> when the detection matches the truth.</returns>
    public static bool IsMatch(RecapInterval detected, RecapInterval truth, double iouMatchThreshold)
        => detected.HasValue && truth.HasValue && IntersectionOverUnion(detected, truth) >= iouMatchThreshold;
}
