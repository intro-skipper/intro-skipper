// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Data;
using Xunit;

/// <summary>
/// The combination rule of ADR-0002 over the shapes measured on real shows. Times are
/// seconds into a 450 second credits window with a 15 second minimum credits duration.
/// </summary>
public class TestCreditsCandidateCombiner
{
    private const double WindowEnd = 450;
    private const int MinimumDuration = 15;
    private static readonly Guid Episode = Guid.NewGuid();

    private static List<AttributedSegment> Combine(params AttributedSegment[] candidates)
        => CreditsCandidateCombiner.Combine(candidates, WindowEnd, MinimumDuration);

    [Fact]
    public void ChromaprintContainsBlackFrame_MergesIntoOneCombinedSegment()
    {
        // Ahsoka: styled credits on dark art, then the black roll; the shared audio spans both
        // and both stop a few seconds before the end.
        var result = Combine(
            Candidate(351, 448, SegmentSource.BlackFrame),
            Candidate(227, 446, SegmentSource.Chromaprint));

        var single = Assert.Single(result);
        Assert.Equal((227, 450, SegmentSource.Combined), Shape(single));
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(20.0)]
    public void CandidatesWithinMergeGap_MergeRegardlessOfOrder(double gap)
    {
        var sharedAudioFirst = Combine(
            Candidate(200, 340, SegmentSource.Chromaprint),
            Candidate(340 + gap, 450, SegmentSource.BlackFrame));
        var blackFirst = Combine(
            Candidate(200, 300, SegmentSource.BlackFrame),
            Candidate(300 + gap, 450, SegmentSource.Chromaprint));

        Assert.Equal((200, 450, SegmentSource.Combined), Shape(Assert.Single(sharedAudioFirst)));
        Assert.Equal((200, 450, SegmentSource.Combined), Shape(Assert.Single(blackFirst)));
    }

    [Fact]
    public void CandidatesSeparatedByContent_StayApartUnderTheirOwnSources()
    {
        // CITY THE ANIMATION E07: ending song, an epilogue scene, then black dubbing cards.
        var result = Combine(
            Candidate(401, 450, SegmentSource.BlackFrame),
            Candidate(90, 179, SegmentSource.Chapter));

        Assert.Equal(
            [(90, 179, SegmentSource.Chapter), (401, 450, SegmentSource.BlackFrame)],
            result.Select(Shape).ToList());
    }

    [Fact]
    public void GapJustPastMergeGap_StaysApart()
    {
        var result = Combine(
            Candidate(100, 200, SegmentSource.Chromaprint),
            Candidate(220.5, 450, SegmentSource.BlackFrame));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ChapterCandidate_IsNeverShortenedByOtherCandidates()
    {
        // CITY THE ANIMATION E08: chaptered ending song, black dubbing cards one second later.
        var result = Combine(
            Candidate(320, 398, SegmentSource.Chromaprint),
            Candidate(309, 398, SegmentSource.Chapter),
            Candidate(399, 450, SegmentSource.BlackFrame));

        Assert.Equal((309, 450, SegmentSource.Combined), Shape(Assert.Single(result)));
    }

    [Fact]
    public void SingleCandidate_KeepsItsSource()
    {
        var result = Combine(Candidate(351, 420, SegmentSource.BlackFrame));

        Assert.Equal((351, 420, SegmentSource.BlackFrame), Shape(Assert.Single(result)));
    }

    [Theory]
    [InlineData(443.0, 450.0)] // Delicious in Dungeon: shared audio stops 7 s before the end
    [InlineData(435.5, 450.0)] // just inside the minimum duration
    [InlineData(435.0, 435.0)] // exactly a minimum duration left: a credits scene still fits
    [InlineData(452.0, 452.0)] // a chapter past the audio's end keeps its own end
    public void CandidateNearTheWindowEnd_IsExtendedToIt(double end, double expectedEnd)
    {
        var result = Combine(Candidate(326, end, SegmentSource.Chromaprint));

        Assert.Equal((326, expectedEnd, SegmentSource.Chromaprint), Shape(Assert.Single(result)));
    }

    [Fact]
    public void InvalidCandidates_AreIgnored()
    {
        var result = Combine(
            new AttributedSegment(new Segment(Episode), SegmentSource.Chromaprint),
            Candidate(351, 420, SegmentSource.BlackFrame));

        Assert.Equal((351, 420, SegmentSource.BlackFrame), Shape(Assert.Single(result)));
        Assert.Empty(Combine());
    }

    private static AttributedSegment Candidate(double start, double end, SegmentSource source)
        => new(new Segment(Episode, new TimeRange(start, end)), source);

    private static (double Start, double End, SegmentSource Source) Shape(AttributedSegment segment)
        => (segment.Segment.Start, segment.Segment.End, segment.Source);
}
