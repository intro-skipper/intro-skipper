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
    // Two fingerprints of 200 samples that differ only at the given positions (shift 0). Starts
    // are past the 5 s snap-to-zero window unless a run begins at sample 0.
    [Theory]
    [InlineData(new[] { 50, 51 }, 0.2, 52, 199)]   // the later, longer run wins
    [InlineData(new[] { 150, 151 }, 0.2, 0, 149)]  // the earlier, longer run wins
    [InlineData(new[] { 99, 100 }, 0.2, 0, 98)]    // equal-length runs: the earlier one wins
    [InlineData(new[] { 100 }, 0.3, 0, 199)]       // a gap within MaximumTimeSkip does not split the run
    public void CompareEpisodes_SelectsTheLongestContiguousRun(int[] mismatches, double maximumTimeSkip, int expectedStartSample, int expectedEndSample)
    {
        var lhs = Enumerable.Range(0, 200).Select(i => (uint)(0x1000 + (i * 0x100))).ToArray();
        var rhs = lhs.Select((point, i) => mismatches.Contains(i) ? point ^ 0xFFu : point).ToArray();
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
