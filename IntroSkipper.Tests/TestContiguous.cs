// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestTimeRanges
{
    // Two fingerprints of 200 samples (25 s) that differ over [gapStart, gapEnd) (shift 0). Starts
    // are past the 5 s snap-to-zero window unless a run begins at sample 0. Runs under 8 s
    // (65 samples) never join across a gap longer than MaximumTimeSkip.
    [Theory]
    [InlineData(50, 52, 0.2, 52, 199)]   // the later, longer run wins
    [InlineData(150, 152, 0.2, 0, 149)]  // the earlier, longer run wins
    [InlineData(60, 140, 0.2, 0, 59)]    // equal-length runs: the earlier one wins
    [InlineData(100, 101, 0.3, 0, 199)]  // a gap within MaximumTimeSkip does not split the run
    public void CompareEpisodes_SelectsTheLongestContiguousRun(int gapStart, int gapEnd, double maximumTimeSkip, int expectedStartSample, int expectedEndSample)
    {
        var lhs = Enumerable.Range(0, 200).Select(i => (uint)(0x1000 + (i * 0x100))).ToArray();
        var rhs = lhs.Select((point, i) => i >= gapStart && i < gapEnd ? point ^ 0xFFu : point).ToArray();
        var analyzer = new ChromaprintAnalyzer(
            NullLogger<ChromaprintAnalyzer>.Instance,
            null!,
            null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            new PluginConfiguration
            {
                MinimumIntroDuration = 1,
                MaximumFingerprintPointDifferences = 0,
                MaximumTimeSkip = maximumTimeSkip,
                InvertedIndexShift = 0,
            });

        var (left, right) = analyzer.CompareEpisodes(Guid.NewGuid(), lhs, Guid.NewGuid(), rhs);

        Assert.Equal(expectedStartSample * ChromaprintConstants.SampleDuration, left.Start);
        Assert.Equal(expectedEndSample * ChromaprintConstants.SampleDuration, left.End);
        Assert.Equal(left.Start, right.Start);
        Assert.Equal(left.End, right.End);
    }

    // Two fingerprints of 600 samples (74 s) that differ over each [start, end) pair in gaps; the
    // second starts with `shift` unrelated samples. A sample is about 0.124 s, so 8 s is about 65
    // samples, and a gap runs from the last similar sample before it to the first after it.
    [Theory]
    [InlineData(new[] { 100, 140 }, 120, 0, 0, 599)]   // 5.1 s gap after a 12.3 s run: joined
    [InlineData(new[] { 100, 160 }, 120, 0, 160, 599)] // 7.6 s gap, over half the 12.3 s run: the longer run wins
    [InlineData(new[] { 50, 60 }, 120, 0, 60, 599)]    // 1.4 s gap after a 6.1 s run: too short to join
    [InlineData(new[] { 480, 520 }, 120, 0, 0, 479)]   // 5.1 s gap before a 9.8 s run: the shorter run sets the limit
    [InlineData(new[] { 70, 100, 170, 220 }, 120, 0, 0, 599)]  // 8.5 s, 8.5 s, 47 s: the first two join, then the pair joins the third
    [InlineData(new[] { 380, 430, 500, 530 }, 120, 0, 0, 599)] // the same runs in reverse order join the same way
    [InlineData(new[] { 100, 140 }, 60, 0, 140, 599)]  // the joined run would pass MaximumIntroDuration: no join
    [InlineData(new[] { 0, 50, 150, 190 }, 120, 7, 50, 592)] // joined on a shifted alignment, which compares the first 593 samples
    public void CompareEpisodes_JoinsALongGapOnlyBetweenTwoLongRuns(int[] gaps, int maximumIntroDuration, int shift, int expectedStartSample, int expectedEndSample)
    {
        var lhs = Enumerable.Range(0, 600).Select(i => (uint)(0x1000 + (i * 0x100))).ToArray();
        var differs = gaps.Chunk(2).ToArray();
        var rhs = Enumerable.Range(0, shift).Select(i => 0xF0000000u + (uint)i)
            .Concat(lhs.Select((point, i) => differs.Any(gap => i >= gap[0] && i < gap[1]) ? point ^ 0xFFu : point))
            .ToArray();
        var analyzer = new ChromaprintAnalyzer(
            NullLogger<ChromaprintAnalyzer>.Instance,
            null!,
            null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            new PluginConfiguration
            {
                MinimumIntroDuration = 1,
                MaximumIntroDuration = maximumIntroDuration,
                MaximumFingerprintPointDifferences = 0,
                MaximumTimeSkip = 0.2,
                InvertedIndexShift = 0,
            });

        var (left, right) = analyzer.CompareEpisodes(Guid.NewGuid(), lhs, Guid.NewGuid(), rhs);

        Assert.Equal(expectedStartSample * ChromaprintConstants.SampleDuration, left.Start);
        Assert.Equal(expectedEndSample * ChromaprintConstants.SampleDuration, left.End);
        Assert.Equal((expectedStartSample + shift) * ChromaprintConstants.SampleDuration, right.Start);
    }

    /// <summary>
    /// Tests that TimeRange intersections are detected correctly.
    /// Tests each time range against a range of 5 to 10 seconds.
    /// </summary>
    [Theory]
    [InlineData(1, 4, false)]   // too early
    [InlineData(4, 6, true)]    // intersects on the left
    [InlineData(7, 8, true)]    // in the middle
    [InlineData(9, 12, true)]   // intersects on the right
    [InlineData(13, 15, false)] // too late
    [InlineData(6, 8, true)]    // fully inside
    [InlineData(3, 12, true)]   // fully contains the range
    [InlineData(5, 10, true)]   // identical range
    [InlineData(0, 5, false)]   // touches the start boundary only
    [InlineData(10, 15, false)] // touches the end boundary only
    public void TestTimeRangeIntersection(int start, int end, bool expected)
    {
        var large = new TimeRange(5, 10);
        var testRange = new TimeRange(start, end);

        Assert.Equal(expected, large.Intersects(testRange));
    }
}
