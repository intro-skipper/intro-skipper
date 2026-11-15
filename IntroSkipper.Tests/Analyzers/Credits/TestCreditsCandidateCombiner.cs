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
/// The combination rule (docs/credits.md) over the shapes
/// measured on real shows. Times are
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

    // Candidates as "start-end Source" separated by ";", and the first stored segment. A part made
    // only of keyframe candidates with more than 180 s of story before the next part that stays
    // goes, however many pieces it arrived in; a part that merged with a chapter or shared audio
    // stays, and the latest part always does. A chapter's end caps the black after it.
    [Theory]
    [InlineData("10-30 BlackFrame; 200-240 BlackFrame", "10-30 BlackFrame")]                                   // 170 s of story, a mid-credits scene
    [InlineData("10-30 BlackFrame; 220-260 BlackFrame", "220-260 BlackFrame")]                                 // 190 s: a text screen inside the episode
    [InlineData("10-30 KeyframeVisuals; 220-260 BlackFrame", "220-260 BlackFrame")]                            // a card run goes the same way
    [InlineData("6-30 KeyframeVisuals; 10-30 BlackFrame; 220-260 BlackFrame", "220-260 BlackFrame")]           // a screen in two pieces goes whole
    [InlineData("10-30 BlackFrame; 100-120 BlackFrame; 310-350 BlackFrame", "310-350 BlackFrame")]             // so do two screens before a far roll
    [InlineData("5-25 Chapter; 10-30 BlackFrame; 220-260 BlackFrame", "5-25 Combined")]                         // a chapter vouches for the black
    [InlineData("5-25 Chromaprint; 10-30 BlackFrame; 220-260 BlackFrame", "5-30 Combined")]                     // so does shared audio over it
    [InlineData("0-5 Chromaprint; 10-30 BlackFrame; 220-260 BlackFrame", "0-30 Combined")]                      // or just before it
    [InlineData("10-30 BlackFrame; 55-150 Chromaprint; 400-440 BlackFrame", "10-30 BlackFrame")]               // shared audio 25 s later is the next part
    [InlineData("10-30 BlackFrame; 120-150 BlackFrame; 300-340 BlackFrame", "10-30 BlackFrame")]               // a black part 90 s later is the next part
    [InlineData("10-30 Chromaprint; 220-260 BlackFrame", "10-30 Chromaprint")]                                 // only keyframe parts go
    public void PartFarBeforeTheNext_GoesUnlessSomethingVouchesForIt(string candidates, string first)
    {
        var result = Combine([.. candidates.Split(';').Select(Parse)]);

        Assert.Equal(Shape(Parse(first)), Shape(result[0]));
        Assert.Equal(Parse(candidates.Split(';')[^1]).Segment.Start, result[^1].Segment.Start);
    }

    // Candidates and every stored segment, as "start-end Source" separated by ";". The episodes are
    // real ones shifted so that their end is the window end. A keyframe part more than 30 s of story
    // before a part that a chapter or shared audio vouches for goes; a mid-credits scene between two
    // keyframe parts keeps both up to 180 s, and nothing changes when the earlier part is shared audio.
    [Theory]
    [InlineData("155.7-179.6 BlackFrame; 359.6-449.9 Chromaprint", "359.6-450 Chromaprint")]                     // Black Clover S2E7: a dark scene 180 s before the ending song
    [InlineData("282-301 BlackFrame; 414-450 Chromaprint", "414-450 Chromaprint")]                               // Adventure Time S3E17: 113 s
    [InlineData("232-330 KeyframeVisuals; 368-420 Chromaprint; 410-450 BlackFrame", "368-450 Combined")]         // Rent-a-Girlfriend S5E4: a card run 38 s before
    [InlineData("311-340 BlackFrame; 380-437 Chromaprint; 417-450 BlackFrame", "380-450 Combined")]              // Lost S1E6: 40 s before
    [InlineData("20-68 BlackFrame; 101-154 BlackFrame; 262-328 BlackFrame; 372-432 Chromaprint; 432-450 BlackFrame", "372-450 Combined")] // Needy Girl Overdose S1E5: three dark scenes go together
    [InlineData("150-178 Chapter; 230-250 BlackFrame; 330-450 Chromaprint", "150-178 Chapter; 330-450 Chromaprint")] // a chapter part stays, the black between goes
    [InlineData("300-330 BlackFrame; 355-450 Chromaprint", "300-330 BlackFrame; 355-450 Chromaprint")]           // a 25 s stinger between two parts stays
    [InlineData("300-330 BlackFrame; 360-450 Chromaprint", "300-330 BlackFrame; 360-450 Chromaprint")]           // so does one of exactly 30 s
    [InlineData("250-278 BlackFrame; 358-450 BlackFrame", "250-278 BlackFrame; 358-450 BlackFrame")]             // Daredevil: Born Again S01E09, an 80 s mid-credits scene
    [InlineData("100-117 Chromaprint; 340-404 BlackFrame", "100-117 Chromaprint; 340-404 BlackFrame")]           // Silo S1E1: shared audio before a black roll stays
    public void KeyframePartBeforeAVouchedPart_GoesPastHalfAMinuteOfStory(string candidates, string stored)
    {
        var result = Combine([.. candidates.Split(';').Select(Parse)]);

        Assert.Equal(stored.Split(';').Select(Parse).Select(Shape).ToList(), result.Select(Shape).ToList());
    }

    private static AttributedSegment Parse(string text)
    {
        var parts = text.Trim().Split(' ');
        var range = parts[0].Split('-').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return Candidate(range[0], range[1], Enum.Parse<SegmentSource>(parts[1]));
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
        // CITY THE ANIMATION E08: chaptered ending song with shared audio inside it, black
        // dubbing cards one second after the chapter ends. The chapter keeps its full range
        // and the cards beyond its authored end stay a segment of their own.
        var result = Combine(
            Candidate(320, 398, SegmentSource.Chromaprint),
            Candidate(309, 398, SegmentSource.Chapter),
            Candidate(399, 450, SegmentSource.BlackFrame));

        Assert.Equal(
            [(309, 398, SegmentSource.Combined), (399, 450, SegmentSource.BlackFrame)],
            result.Select(Shape).ToList());
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
    public void CreditsChapterEnd_CapsAndBlocksOtherCandidates()
    {
        // "Ending" 350 to 438 followed by a named "Preview" chapter at 438: black frames that
        // run into the preview are capped at it, and nothing extends across it.
        var result = Combine(
            Candidate(350, 438, SegmentSource.Chapter),
            Candidate(400, 449.5, SegmentSource.BlackFrame),
            Candidate(300, 436, SegmentSource.Chromaprint));

        Assert.Equal((300, 438, SegmentSource.Combined), Shape(Assert.Single(result)));
    }

    [Fact]
    public void CreditsChapterEnd_SeparatesACandidateBeyondIt()
    {
        // CITY THE ANIMATION E08 with its "epilogue" chapter: the dubbing cards after the
        // ending chapter stay a segment of their own instead of merging across the boundary.
        var result = Combine(
            Candidate(309, 398, SegmentSource.Chapter),
            Candidate(399, 450, SegmentSource.BlackFrame));

        Assert.Equal(
            [(309, 398, SegmentSource.Chapter), (399, 450, SegmentSource.BlackFrame)],
            result.Select(Shape).ToList());
    }

    [Fact]
    public void CappedCandidateBelowMinimumDuration_IsDropped()
    {
        var result = Combine(
            Candidate(350, 438, SegmentSource.Chapter),
            Candidate(430, 450, SegmentSource.BlackFrame));

        Assert.Equal((350, 438, SegmentSource.Chapter), Shape(Assert.Single(result)));
    }

    [Fact]
    public void ATrailingRollAfterTheCreditsChapterEnd_IsNotCappedByIt()
    {
        // "Ending" 150 to 210, content, then a black roll from 350: the roll is its own
        // segment and keeps its full length.
        var result = Combine(
            Candidate(150, 210, SegmentSource.Chapter),
            Candidate(350, 449.5, SegmentSource.BlackFrame));

        Assert.Equal(
            [(150, 210, SegmentSource.Chapter), (350, 450, SegmentSource.BlackFrame)],
            result.Select(Shape).ToList());
    }

    [Fact]
    public void AdjacentCreditsChapters_ShareNoHardBoundary()
    {
        var result = Combine(
            Candidate(300, 350, SegmentSource.Chapter),
            Candidate(350, 450, SegmentSource.Chapter));

        Assert.Equal((300, 450, SegmentSource.Combined), Shape(Assert.Single(result)));
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
