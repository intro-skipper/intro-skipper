// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Unit tests + microbenchmark for the RFC B cross-episode reuse-matching spike
/// (<see cref="CrossEpisodeReuseMatcher"/>). Operates entirely on synthetic <c>uint[]</c> fingerprints,
/// so it needs no FFmpeg or media files.
/// </summary>
public class TestCrossEpisodeReuse(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void SecondsToPoints_MatchesChromaprintHopRate()
    {
        // ~8.075 points/second. A 24-minute episode is ~11.6k points.
        Assert.Equal(8, CrossEpisodeReuseMatcher.SecondsToPoints(1.0));
        Assert.InRange(CrossEpisodeReuseMatcher.SecondsToPoints(1440), 11_600, 11_650);
    }

    [Fact]
    public void FindReusedSpans_LocatesSinglePlantedSpanDeepInReference()
    {
        var rng = new Random(1);
        var reference = RandomFingerprint(12_000, rng); // ~24 min prior episode (full)
        var query = RandomFingerprint(970, rng);        // ~120 s opening of episode N

        // Plant a reused clip: query[100..399] is footage taken from reference[8100..8399].
        const int queryStart = 100;
        const int referenceStart = 8100;
        const int length = 300; // ~37 s
        Plant(query, queryStart, reference, referenceStart, length);

        var (spans, diag) = CrossEpisodeReuseMatcher.FindReusedSpans(query, reference);

        Assert.False(diag.EarlyExit);
        var span = Assert.Single(spans);
        Assert.Equal(queryStart, span.QueryStart);
        Assert.Equal(queryStart + length - 1, span.QueryEnd);
        Assert.Equal(referenceStart, span.ReferenceStart);
        Assert.Equal(referenceStart - queryStart, span.Shift);
    }

    [Fact]
    public void FindReusedSpans_ToleratesMildReencodeNoise()
    {
        var rng = new Random(2);
        var reference = RandomFingerprint(12_000, rng);
        var query = RandomFingerprint(970, rng);

        const int queryStart = 120;
        const int length = 260;
        Plant(query, queryStart, reference, 5_000, length);

        // Model a mild re-encode: ~40% of points drift by 1-5 bits (within the 6-bit similarity
        // threshold, so extraction still matches them) while ~60% survive byte-identical (so the
        // shift-discovery phase, which probes only +/- IndexShift in VALUE space, still finds the shift).
        PerturbForReencode(query, queryStart, length, fraction: 0.40, maxBitsPerPoint: 5, rng);

        var (spans, _) = CrossEpisodeReuseMatcher.FindReusedSpans(query, reference);

        var span = Assert.Single(spans);
        Assert.Equal(queryStart, span.QueryStart);
        Assert.Equal(queryStart + length - 1, span.QueryEnd);
    }

    [Fact]
    public void FindReusedSpans_RejectsHeavilyAlteredAudio()
    {
        var rng = new Random(3);
        var reference = RandomFingerprint(12_000, rng);
        var query = RandomFingerprint(970, rng);

        const int queryStart = 120;
        const int length = 260;
        Plant(query, queryStart, reference, 5_000, length);

        // A re-mixed recap (music bed / voiceover laid over the clip) diverges far beyond the
        // 6-bit threshold: flip 12 distinct bits on every point. The reuse must no longer be detectable.
        FlipBitsEveryPoint(query, queryStart, length, bitsToFlip: 12, rng);

        var (spans, _) = CrossEpisodeReuseMatcher.FindReusedSpans(query, reference);

        Assert.DoesNotContain(spans, s => s.QueryStart <= queryStart + length && queryStart <= s.QueryEnd);
    }

    [Fact]
    public void AssembleRecap_StitchesMontageOfThreeClipsIntoOneBoundary()
    {
        var rng = new Random(4);
        var reference = RandomFingerprint(12_000, rng);
        var query = RandomFingerprint(970, rng);

        // A "previously on" montage: three clips drawn from three different points of the prior
        // episode, laid back-to-back near the start of episode N (each separated by a ~1 s cut).
        var clip1 = PlantSeconds(query, reference, queryStartSeconds: 5, referenceStartSeconds: 600, durationSeconds: 12);
        PlantSeconds(query, reference, queryStartSeconds: 18, referenceStartSeconds: 950, durationSeconds: 10);
        var clip3 = PlantSeconds(query, reference, queryStartSeconds: 29, referenceStartSeconds: 1300, durationSeconds: 13);

        var (spans, _) = CrossEpisodeReuseMatcher.FindReusedSpans(query, reference);

        // Three disjoint clips, three distinct shifts.
        Assert.Equal(3, spans.Count);
        Assert.Equal(3, spans.Select(s => s.Shift).Distinct().Count());

        var recap = CrossEpisodeReuseMatcher.AssembleRecap(spans);
        Assert.NotNull(recap);

        // The hull spans from the first clip's start to the last clip's end, NOT forced to 0.
        Assert.True(recap!.Start > 1.0, FormattableString.Invariant($"recap.Start={recap.Start} should not be snapped to 0"));
        Assert.Equal(clip1.StartSeconds, recap.Start, precision: 0);
        Assert.Equal(clip3.EndSeconds, recap.End, precision: 0);
    }

    [Fact]
    public void FindReusedSpans_EarlyExitsOnUnrelatedEpisodes()
    {
        var rng = new Random(5);
        var reference = RandomFingerprint(12_000, rng);
        var query = RandomFingerprint(970, rng); // independent content, nothing reused

        var (spans, diag) = CrossEpisodeReuseMatcher.FindReusedSpans(query, reference);

        Assert.True(diag.EarlyExit, FormattableString.Invariant($"expected early exit; overlap was {diag.PointSetOverlap:P2}"));
        Assert.Empty(spans);
        Assert.Null(CrossEpisodeReuseMatcher.AssembleRecap(spans));
    }

    /// <summary>
    /// The make-or-break structural claim of the RFC: intro detection is opening-vs-opening at the SAME
    /// offset, while a recap reuses footage from ANYWHERE in a prior episode (a large offset). The shipped
    /// <see cref="ChromaprintAnalyzer.CompareEpisodes"/> can find the former but not the latter; the spike
    /// finds both.
    /// </summary>
    [Fact]
    public void Production_FindsAlignedReuse_ButMissesDeepReuse_WhereSpikeSucceeds()
    {
        var analyzer = new ChromaprintAnalyzer(NullLogger<ChromaprintAnalyzer>.Instance, null!, null!);
        var lhsId = Guid.NewGuid();
        var rhsId = Guid.NewGuid();
        const int length = 300; // ~37 s, comfortably above the 15 s intro floor

        // CASE A — intro-like: shared block sits at nearly the SAME offset in both episodes (shift ~50).
        var aligned = NewAlignedPair(seed: 10, length, lhsStart: 100, rhsStart: 150);
        var (alignedLhs, _) = analyzer.CompareEpisodes(lhsId, aligned.Lhs, rhsId, aligned.Rhs);
        Assert.True(alignedLhs.Valid, "production should detect a reused block at a small (intro-like) offset");

        // CASE B — recap-like: the SAME block is reused deep inside the prior episode (shift ~8000),
        // with episode N's side being only a short opening window.
        var deep = NewAlignedPair(seed: 10, length, lhsStart: 100, rhsStart: 8100, rhsLength: 12_000, lhsLength: 970);
        var (deepLhs, _) = analyzer.CompareEpisodes(lhsId, deep.Lhs, rhsId, deep.Rhs);
        Assert.False(deepLhs.Valid, "production's FindContiguous cannot reach a deep-offset reuse (min(len)-|shift| < 0)");

        // The spike recovers the deep reuse the production engine misses.
        var (spans, _) = CrossEpisodeReuseMatcher.FindReusedSpans(deep.Lhs, deep.Rhs);
        var span = Assert.Single(spans);
        Assert.Equal(100, span.QueryStart);
        Assert.Equal(8000, span.Shift);
    }

    /// <summary>
    /// Microbenchmark substantiating the RFC's cost analysis. Measures wall time, distinct shift count,
    /// and pre-filter overlap on realistic fingerprint sizes. Writes a markdown snippet to a temp file
    /// (and the test log) so the numbers can be transcribed into the RFC.
    /// </summary>
    [Fact]
    public void Benchmark_RealisticSizes_IsCheap()
    {
        var report = new StringBuilder();
        report.AppendLine("| prior-episode size | query window | distinct shifts | shifts scanned | spans | avg ms / pair |");
        report.AppendLine("|---|---|---|---|---|---|");

        // (full prior-episode points, label)
        var sizes = new (int Points, string Label)[]
        {
            (10_659, "22 min (1320 s)"),
            (11_628, "24 min (1440 s)"),
            (20_349, "42 min (2520 s)"),
            (29_070, "60 min (3600 s)"),
        };

        const int queryPoints = 970; // ~120 s opening window
        const int iterations = 200;

        foreach (var (points, label) in sizes)
        {
            var rng = new Random(99);
            var reference = RandomFingerprint(points, rng);
            var query = RandomFingerprint(queryPoints, rng);

            // Plant a 3-clip montage so the timed path includes real extraction work.
            PlantSeconds(query, reference, 5, 600, 12);
            PlantSeconds(query, reference, 18, Math.Min(950, points / 12), 10);
            PlantSeconds(query, reference, 29, Math.Min(1300, points / 9), 13);

            // Warm up (JIT) and capture a representative result.
            var (spans, diag) = CrossEpisodeReuseMatcher.FindReusedSpans(query, reference);

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                CrossEpisodeReuseMatcher.FindReusedSpans(query, reference);
            }

            sw.Stop();

            var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
            report.AppendLine(CultureInfo.InvariantCulture, $"| {label} ({points} pts) | {queryPoints} pts | {diag.DistinctShiftsDiscovered} | {diag.ShiftsScanned} | {spans.Count} | {avgMs:F3} |");

            // A single audio comparison must be cheap; even the largest case must stay well under 50 ms.
            Assert.True(avgMs < 50, FormattableString.Invariant($"reuse matching took {avgMs:F2} ms for {label}, expected < 50 ms"));
            Assert.True(spans.Count >= 1, FormattableString.Invariant($"expected to recover planted montage for {label}"));
        }

        var text = report.ToString();
        _output.WriteLine(text);

        var path = Path.Combine(Path.GetTempPath(), "recap-rfc-b-bench.md");
        File.WriteAllText(path, text);
        _output.WriteLine(FormattableString.Invariant($"benchmark table written to {path}"));
    }

    private static uint[] RandomFingerprint(int length, Random rng)
    {
        var fingerprint = new uint[length];
        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < length; i++)
        {
            rng.NextBytes(bytes);
            fingerprint[i] = BitConverter.ToUInt32(bytes);
        }

        return fingerprint;
    }

    private static void Plant(uint[] destination, int destinationStart, uint[] source, int sourceStart, int length)
        => Array.Copy(source, sourceStart, destination, destinationStart, length);

    private static (double StartSeconds, double EndSeconds) PlantSeconds(
        uint[] query,
        uint[] reference,
        double queryStartSeconds,
        double referenceStartSeconds,
        double durationSeconds)
    {
        var queryStart = CrossEpisodeReuseMatcher.SecondsToPoints(queryStartSeconds);
        var referenceStart = CrossEpisodeReuseMatcher.SecondsToPoints(referenceStartSeconds);
        var length = CrossEpisodeReuseMatcher.SecondsToPoints(durationSeconds);
        Plant(query, queryStart, reference, referenceStart, length);
        return (
            queryStart * ChromaprintConstants.SampleDuration,
            (queryStart + length) * ChromaprintConstants.SampleDuration);
    }

    // Mild re-encode: only a fraction of points drift, each by a few bits within the similarity
    // threshold; the rest stay byte-identical so the shift-discovery phase still anchors.
    private static void PerturbForReencode(uint[] array, int start, int length, double fraction, int maxBitsPerPoint, Random rng)
    {
        for (var i = start; i < start + length; i++)
        {
            if (rng.NextDouble() >= fraction)
            {
                continue;
            }

            FlipDistinctBits(array, i, 1 + rng.Next(maxBitsPerPoint), rng);
        }
    }

    // Heavy alteration: flip the same number of distinct bits on every point (defeats both phases).
    private static void FlipBitsEveryPoint(uint[] array, int start, int length, int bitsToFlip, Random rng)
    {
        for (var i = start; i < start + length; i++)
        {
            FlipDistinctBits(array, i, bitsToFlip, rng);
        }
    }

    private static void FlipDistinctBits(uint[] array, int index, int bitsToFlip, Random rng)
    {
        var chosen = new HashSet<int>();
        while (chosen.Count < bitsToFlip)
        {
            chosen.Add(rng.Next(32));
        }

        foreach (var bit in chosen)
        {
            array[index] ^= 1u << bit;
        }
    }

    private static (uint[] Lhs, uint[] Rhs) NewAlignedPair(
        int seed,
        int length,
        int lhsStart,
        int rhsStart,
        int rhsLength = 1_000,
        int lhsLength = 1_000)
    {
        var rng = new Random(seed);
        var lhs = RandomFingerprint(lhsLength, rng);
        var rhs = RandomFingerprint(rhsLength, rng);
        Plant(lhs, lhsStart, rhs, rhsStart, length);
        return (lhs, rhs);
    }
}
