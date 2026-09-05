// SPDX-FileCopyrightText: 2024-2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using Xunit;

public class TestRecapDetection
{
    [Fact]
    public void SelectSharedRegion_Introduction_PicksLongestRegion()
    {
        var (lhs, rhs) = TwoSharedRegions();

        var (lhsSegment, _) = ChromaprintAnalyzer.SelectSharedRegion(
            Guid.NewGuid(),
            lhs,
            Guid.NewGuid(),
            rhs,
            AnalysisMode.Introduction);

        Assert.Equal(40, lhsSegment.Start);
        Assert.Equal(65, lhsSegment.End);
    }

    [Fact]
    public void SelectSharedRegion_Recap_PicksEarliestRegion()
    {
        var (lhs, rhs) = TwoSharedRegions();

        var (lhsSegment, rhsSegment) = ChromaprintAnalyzer.SelectSharedRegion(
            Guid.NewGuid(),
            lhs,
            Guid.NewGuid(),
            rhs,
            AnalysisMode.Recap);

        Assert.Equal(10, lhsSegment.Start);
        Assert.Equal(16, lhsSegment.End);
        Assert.Equal(11, rhsSegment.Start);
        Assert.Equal(17, rhsSegment.End);
    }

    [Fact]
    public void SelectSharedRegion_Recap_ReturnsInvalidSegments_WhenRangesAreEmpty()
    {
        var lhsId = Guid.NewGuid();
        var rhsId = Guid.NewGuid();

        var (lhsSegment, rhsSegment) = ChromaprintAnalyzer.SelectSharedRegion(
            lhsId,
            [],
            rhsId,
            [],
            AnalysisMode.Recap);

        Assert.Equal(lhsId, lhsSegment.EpisodeId);
        Assert.Equal(rhsId, rhsSegment.EpisodeId);
        Assert.False(lhsSegment.Valid);
        Assert.False(rhsSegment.Valid);
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction, 0)]
    [InlineData(AnalysisMode.Recap, 3)]
    public void SelectSharedRegion_SnapsNearZeroStart_ExceptForRecap(AnalysisMode mode, double expectedStart)
    {
        var (lhsSegment, rhsSegment) = ChromaprintAnalyzer.SelectSharedRegion(
            Guid.NewGuid(),
            [new TimeRange(3, 20)],
            Guid.NewGuid(),
            [new TimeRange(3, 20)],
            mode);

        Assert.Equal(expectedStart, lhsSegment.Start);
        Assert.Equal(expectedStart, rhsSegment.Start);
    }

    // Black frames at 28 s (fade after a cold open), 50 s (inside the recap) and 90 s (montage
    // end); the sting is 5 s long and the scan window ends at 120 s.
    [Theory]
    [InlineData(true, 35, 28)]
    [InlineData(false, 35, 0)]
    [InlineData(true, 4, 0)]
    [InlineData(true, 75, 75)]
    public void BuildRecapFromSting_StartsAtColdOpenFadeOnlyWhenAnchored(bool anchor, double stingStart, double expectedStart)
    {
        var episodeId = Guid.NewGuid();
        BlackFrame[] frames = [new(100, 28, 0), new(100, 50, 1), new(100, 90, 2)];

        var recap = RecapDetectionHelper.BuildRecapFromSting(
            episodeId,
            new Segment(episodeId, new TimeRange(stingStart, stingStart + 5)),
            frames,
            minimumRecapDuration: 15,
            maximumRecapBoundary: 120,
            anchor);

        Assert.NotNull(recap);
        Assert.Equal(expectedStart, recap.Start);
        Assert.Equal(90, recap.End);
    }

    [Fact]
    public void BuildRecapFromSting_ReturnsNull_WhenNoBlackFrameClosesTheRecap()
    {
        var episodeId = Guid.NewGuid();

        var recap = RecapDetectionHelper.BuildRecapFromSting(
            episodeId,
            new Segment(episodeId, new TimeRange(35, 40)),
            [new BlackFrame(100, 28, 0)],
            minimumRecapDuration: 15,
            maximumRecapBoundary: 120,
            anchorToColdOpen: true);

        Assert.Null(recap);
    }

    [Fact]
    public void RecapFingerprintRange_UsesIntroFingerprintWindow()
    {
        var episode = new QueuedEpisode
        {
            Duration = 1800,
            IntroFingerprintEnd = 240,
            CreditsFingerprintStart = 1500,
            CreditsFingerprintEnd = 1800,
        };

        var range = episode.GetFingerprintRange(AnalysisMode.Recap);

        Assert.Equal((0d, 240d), range);
    }

    private static (List<TimeRange> Lhs, List<TimeRange> Rhs) TwoSharedRegions()
    {
        return (
            [new TimeRange(10, 16), new TimeRange(40, 65)],
            [new TimeRange(11, 17), new TimeRange(41, 66)]);
    }
}
