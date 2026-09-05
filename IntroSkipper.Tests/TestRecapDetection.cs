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
