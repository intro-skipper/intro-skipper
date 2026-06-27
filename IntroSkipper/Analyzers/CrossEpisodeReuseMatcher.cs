// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Data;

namespace IntroSkipper.Analyzers;

/// <summary>
/// RESEARCH SPIKE (RFC B): cross-episode content-reuse matching for recap detection.
///
/// <para>
/// This is a self-contained, dependency-free prototype that proves the core mechanism behind
/// <c>docs/recap-research/B-cross-episode.md</c>. It is NOT wired into the analyzer chain. It locates
/// reused audio sub-segments of a "query" fingerprint (the opening of episode N) inside a "reference"
/// fingerprint (a PRIOR episode's full fingerprint) and assembles the disjoint reused spans of a
/// montage into a single recap boundary.
/// </para>
///
/// <para>
/// It deliberately mirrors the primitives the production <see cref="ChromaprintAnalyzer"/> already
/// uses — an inverted index of fingerprint points, a per-shift XOR/popcount comparison, and a
/// longest-contiguous-run extraction — so the cost and behaviour map directly onto the shipped engine.
/// The one structural change required for reuse matching (vs the shipped intro matcher) is documented
/// on <see cref="ExtractContiguousRunAtShift"/>.
/// </para>
/// </summary>
public static class CrossEpisodeReuseMatcher
{
    /// <summary>
    /// Converts a duration in seconds to a number of fingerprint points.
    /// </summary>
    /// <param name="seconds">Duration in seconds.</param>
    /// <returns>Equivalent number of fingerprint points (rounded).</returns>
    public static int SecondsToPoints(double seconds) =>
        (int)Math.Round(seconds / ChromaprintConstants.SampleDuration);

    /// <summary>
    /// Cheap coarse pre-filter: the fraction of the query's distinct point values that also appear
    /// in the reference. This is the prototype's stand-in for a MinHash estimate; on real data a
    /// MinHash sketch gives the same signal in O(k) instead of O(n).
    /// </summary>
    /// <param name="query">Query fingerprint (opening of episode N).</param>
    /// <param name="reference">Reference fingerprint (prior episode).</param>
    /// <returns>Overlap fraction in [0, 1].</returns>
    public static double PointSetOverlap(uint[] query, uint[] reference)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(reference);

        if (query.Length == 0)
        {
            return 0;
        }

        var referenceSet = new HashSet<uint>(reference);
        var queryDistinct = new HashSet<uint>(query);
        var shared = queryDistinct.Count(referenceSet.Contains);
        return (double)shared / queryDistinct.Count;
    }

    /// <summary>
    /// Builds a multimap inverted index (point value -> every index it occurs at) for the reference.
    /// The shipped <see cref="ChromaprintAnalyzer.CreateInvertedIndex"/> keeps only the LAST occurrence,
    /// which is adequate for opening-vs-opening intro matching but loses reused occurrences when the
    /// reference is a full episode; reuse matching needs every occurrence to vote.
    /// </summary>
    /// <param name="reference">Reference fingerprint.</param>
    /// <returns>Multimap of point value to ascending indices.</returns>
    public static Dictionary<uint, List<int>> BuildMultimapIndex(uint[] reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var index = new Dictionary<uint, List<int>>(reference.Length);
        for (var i = 0; i < reference.Length; i++)
        {
            if (!index.TryGetValue(reference[i], out var list))
            {
                list = [];
                index[reference[i]] = list;
            }

            list.Add(i);
        }

        return index;
    }

    /// <summary>
    /// Locates every reused span of <paramref name="query"/> inside <paramref name="reference"/>.
    /// </summary>
    /// <param name="query">Query fingerprint — the opening window of episode N.</param>
    /// <param name="reference">Reference fingerprint — a prior episode's FULL fingerprint.</param>
    /// <param name="options">Tuning parameters (uses production-equivalent defaults when null).</param>
    /// <returns>Reused spans (query coordinates) and cost diagnostics.</returns>
    public static (IReadOnlyList<ReusedSpan> Spans, ReuseMatchDiagnostics Diagnostics) FindReusedSpans(
        uint[] query,
        uint[] reference,
        ReuseMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(reference);
        options ??= new ReuseMatchOptions();

        var overlap = PointSetOverlap(query, reference);
        if (query.Length == 0 || reference.Length == 0 || overlap < options.PreFilterMinOverlap)
        {
            return ([], new ReuseMatchDiagnostics(0, 0, overlap, EarlyExit: true));
        }

        var referenceIndex = BuildMultimapIndex(reference);

        // Shift voting: every query point votes for the offset (referenceIndex - queryIndex) of each
        // near-equal reference occurrence. A genuinely reused clip of length L produces ~L votes at a
        // single shift, dwarfing the ~1-2 votes random coincidences scatter across many shifts.
        var votes = new Dictionary<int, int>();
        for (var q = 0; q < query.Length; q++)
        {
            var basePoint = query[q];
            for (var delta = -options.IndexShift; delta <= options.IndexShift; delta++)
            {
                var probe = unchecked((uint)(basePoint + delta));
                if (!referenceIndex.TryGetValue(probe, out var occurrences))
                {
                    continue;
                }

                var voteBudget = Math.Min(occurrences.Count, options.MaxVotesPerPoint);
                for (var k = 0; k < voteBudget; k++)
                {
                    var shift = occurrences[k] - q;
                    votes[shift] = votes.GetValueOrDefault(shift) + 1;
                }
            }
        }

        var distinctShifts = votes.Count;

        // Only the strongest shifts are fully scanned; this caps the expensive phase.
        var scannedShifts = votes
            .OrderByDescending(static kvp => kvp.Value)
            .Take(options.TopShifts)
            .Select(static kvp => kvp.Key)
            .ToList();

        var spans = new List<ReusedSpan>();
        foreach (var shift in scannedShifts)
        {
            var span = ExtractContiguousRunAtShift(query, reference, shift, options);
            if (span is not null)
            {
                spans.Add(span.Value);
            }
        }

        // Deduplicate spans that overlap heavily in query space (different shifts can re-find the same
        // clip when the reference contains near-duplicate audio); keep the longest per query region.
        var deduped = DeduplicateByQueryOverlap(spans);

        return (deduped, new ReuseMatchDiagnostics(distinctShifts, scannedShifts.Count, overlap, EarlyExit: false));
    }

    /// <summary>
    /// Finds the longest contiguous run of matching points between query and reference at a fixed shift.
    ///
    /// <para>
    /// This is the corrected analogue of <see cref="ChromaprintAnalyzer"/>'s private <c>FindContiguous</c>.
    /// The shipped version computes its scan length as <c>min(lhs, rhs) - |shift|</c>, which assumes both
    /// fingerprints are of comparable length and aligned near the front (true for intros). When the query
    /// is a short opening window and the reference is a full episode, a reused clip sitting deep in the
    /// reference implies a large shift, and <c>min(len) - |shift|</c> goes negative — so the shipped loop
    /// never runs and the reuse is invisible. Here the valid query range is derived from the actual index
    /// bounds of BOTH arrays, so arbitrarily large shifts work.
    /// </para>
    /// </summary>
    /// <param name="query">Query fingerprint.</param>
    /// <param name="reference">Reference fingerprint.</param>
    /// <param name="shift">Reference-minus-query index offset to test.</param>
    /// <param name="options">Tuning parameters.</param>
    /// <returns>The longest qualifying run, or null when none reaches <see cref="ReuseMatchOptions.MinRunPoints"/>.</returns>
    public static ReusedSpan? ExtractContiguousRunAtShift(
        uint[] query,
        uint[] reference,
        int shift,
        ReuseMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(options);

        // referenceIndex = queryIndex + shift must be in [0, reference.Length).
        var qStart = Math.Max(0, -shift);
        var qEnd = Math.Min(query.Length, reference.Length - shift); // exclusive
        if (qEnd - qStart < options.MinRunPoints)
        {
            return null;
        }

        var bestStart = -1;
        var bestEnd = -1;
        var bestMatches = 0;

        var runStart = -1;
        var lastMatch = -1;
        var runMatches = 0;

        for (var q = qStart; q < qEnd; q++)
        {
            var diff = query[q] ^ reference[q + shift];
            var similar = ChromaprintAnalyzer.CountBits(diff) <= options.MaxBitDifferences;

            if (similar)
            {
                if (runStart < 0)
                {
                    runStart = q;
                    runMatches = 0;
                }

                lastMatch = q;
                runMatches++;
                continue;
            }

            // A non-matching point only breaks the run if the gap since the last match exceeds the
            // permitted skip (mirrors MaximumTimeSkip tolerance in production).
            if (runStart >= 0 && q - lastMatch > options.MaxGapPoints)
            {
                CommitRun(runStart, lastMatch, runMatches, ref bestStart, ref bestEnd, ref bestMatches);
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            CommitRun(runStart, lastMatch, runMatches, ref bestStart, ref bestEnd, ref bestMatches);
        }

        // Require enough GENUINE matches (not merely a wide first-to-last span): real reuse is ~100%
        // dense, whereas coincidental matches at a spurious shift are extremely sparse. This rejects the
        // degenerate case where two unrelated points happen to fall within MaxGapPoints of each other.
        if (bestMatches < options.MinRunPoints)
        {
            return null;
        }

        return new ReusedSpan(bestStart, bestEnd, bestStart + shift, shift);
    }

    /// <summary>
    /// Assembles a set of reused spans into a single recap boundary by clustering spans that are
    /// contiguous in episode N (a montage = several back-to-back reused clips). The recap is the hull
    /// of the earliest qualifying cluster. The start is NOT forced to 0 — the boundary tracks the actual
    /// reused content, so a cold open before the "previously on" is excluded.
    /// </summary>
    /// <param name="spans">Reused spans (query coordinates).</param>
    /// <param name="options">Tuning parameters (uses defaults when null).</param>
    /// <returns>The recap time range in seconds, or null when no cluster qualifies.</returns>
    public static TimeRange? AssembleRecap(IReadOnlyList<ReusedSpan> spans, ReuseMatchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(spans);
        options ??= new ReuseMatchOptions();

        if (spans.Count == 0)
        {
            return null;
        }

        var ordered = spans.OrderBy(static s => s.QueryStart).ToList();

        // Greedily merge spans whose query-space gap is small into clusters.
        var clusters = new List<(int Start, int End)>();
        var clusterStart = ordered[0].QueryStart;
        var clusterEnd = ordered[0].QueryEnd;

        for (var i = 1; i < ordered.Count; i++)
        {
            var span = ordered[i];
            if (span.QueryStart - clusterEnd <= options.MaxMontageGapPoints)
            {
                clusterEnd = Math.Max(clusterEnd, span.QueryEnd);
            }
            else
            {
                clusters.Add((clusterStart, clusterEnd));
                clusterStart = span.QueryStart;
                clusterEnd = span.QueryEnd;
            }
        }

        clusters.Add((clusterStart, clusterEnd));

        // Recaps lead the episode: choose the earliest cluster that is at least one clip long.
        foreach (var (start, end) in clusters.OrderBy(static c => c.Start))
        {
            if (end - start + 1 >= options.MinRunPoints)
            {
                return new TimeRange(
                    start * ChromaprintConstants.SampleDuration,
                    (end + 1) * ChromaprintConstants.SampleDuration);
            }
        }

        return null;
    }

    private static void CommitRun(int start, int end, int matches, ref int bestStart, ref int bestEnd, ref int bestMatches)
    {
        if (matches > bestMatches)
        {
            bestMatches = matches;
            bestStart = start;
            bestEnd = end;
        }
    }

    private static List<ReusedSpan> DeduplicateByQueryOverlap(List<ReusedSpan> spans)
    {
        var ordered = spans
            .OrderByDescending(static s => s.LengthPoints)
            .ToList();

        var kept = new List<ReusedSpan>();
        foreach (var span in ordered)
        {
            var overlaps = kept.Any(k => span.QueryStart <= k.QueryEnd && k.QueryStart <= span.QueryEnd);
            if (!overlaps)
            {
                kept.Add(span);
            }
        }

        return [.. kept.OrderBy(static s => s.QueryStart)];
    }
}
