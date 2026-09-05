// SPDX-FileCopyrightText: 2024-2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
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

    // An anchored recap (start 28) and a 0:00 recap for the same episode share their end at 90.
    [Theory]
    [InlineData(AnalysisMode.Recap, true, 28, 0, true)]
    [InlineData(AnalysisMode.Recap, true, 0, 28, false)]
    [InlineData(AnalysisMode.Recap, false, 28, 0, false)]
    [InlineData(AnalysisMode.Introduction, true, 28, 0, false)]
    public void IsBetterCandidate_PrefersAnchoredRecapOnlyWhenAnchoring(
        AnalysisMode mode,
        bool anchor,
        double candidateStart,
        double savedStart,
        bool expected)
    {
        var episodeId = Guid.NewGuid();

        var better = ChromaprintAnalyzer.IsBetterCandidate(
            new Segment(episodeId, new TimeRange(candidateStart, 90)),
            new Segment(episodeId, new TimeRange(savedStart, 90)),
            mode,
            anchor,
            endSnapThreshold: 2);

        Assert.Equal(expected, better);
    }

    [Theory]
    [InlineData(2, 1, 0, false, 0)]
    [InlineData(2, 0, 1, true, 0)]
    [InlineData(2, 2, 0, false, 0)]
    [InlineData(2, 2.001, 0, false, 0)]
    [InlineData(2, 2.002, 0, true, 2.002)]
    [InlineData(2, 0, 2.002, false, 2.002)]
    [InlineData(5, 4, 0, false, 0)]
    [InlineData(0, 0.001, 0, false, 0)]
    [InlineData(0, 0.002, 0, true, 0.002)]
    [InlineData(2, 28, 1, true, 28)]
    [InlineData(2, 1, 28, false, 28)]
    [InlineData(2, 28, 35, true, 28)]
    [InlineData(2, 35, 28, false, 28)]
    public async Task IsBetterCandidate_PrefersOnlyAnchorsThatSurviveStartSnapping(
        double snapThreshold,
        double candidateStart,
        double savedStart,
        bool expectedBetter,
        double expectedAdjustedStart)
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 300 };
        var candidate = new Segment(episode.EpisodeId, new TimeRange(candidateStart, 90));
        var saved = new Segment(episode.EpisodeId, new TimeRange(savedStart, 90));
        var config = new PluginConfiguration
        {
            AnchorRecapToColdOpen = true,
            EndSnapThreshold = snapThreshold,
            AdjustIntroBasedOnChapters = false,
            AdjustIntroBasedOnSilence = false,
            SnapToKeyframe = false,
            IntroStartOffset = 0,
            IntroEndOffset = 0,
        };

        var better = ChromaprintAnalyzer.IsBetterCandidate(
            candidate, saved, AnalysisMode.Recap, config.AnchorRecapToColdOpen, config.EndSnapThreshold);
        var helper = new TimeAdjustmentHelper(NullLogger.Instance, config, AnalysisMode.Recap, null!);
        var adjusted = await helper.AdjustIntroTimesAsync(episode, better ? candidate : saved);

        Assert.Equal(expectedBetter, better);
        Assert.Equal(expectedAdjustedStart, adjusted.Start);
        Assert.Equal(90, adjusted.End);
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

    // Black frames at 28 and 41 close a 0-41 recap, but anchoring to 28 leaves 13 s, under the minimum.
    [Fact]
    public void BuildRecapFromSting_ReturnsNull_WhenAnchoredRecapIsShorterThanMinimum()
    {
        var episodeId = Guid.NewGuid();

        var recap = RecapDetectionHelper.BuildRecapFromSting(
            episodeId,
            new Segment(episodeId, new TimeRange(35, 40)),
            [new BlackFrame(100, 28, 0), new BlackFrame(100, 41, 1)],
            minimumRecapDuration: 15,
            maximumRecapBoundary: 120,
            anchorToColdOpen: true);

        Assert.Null(recap);
    }

    // Episode A opens with a logo that B also has at 0:00 and, after a cold open, a sting at 35 s
    // that C also has. Black frames sit at 28 s (the fade before the sting), 50 s and 90 s.
    // Whichever pair the analyzer meets first, the recap saved for A must start at the fade.
    [Theory]
    [InlineData("A,B,C")]
    [InlineData("A,C,B")]
    public async Task AnalyzeMediaFiles_AnchoredRecapSurvivesAnEarlierPairMatchingAtEpisodeStart(string order)
    {
        var fingerprints = new Dictionary<string, uint[]>
        {
            ["A"] = Fingerprint(0x10000, (0, 5, 0x1000), (35, 40, 0x2000)),
            ["B"] = Fingerprint(0x20000, (0, 5, 0x1000)),
            ["C"] = Fingerprint(0x30000, (35, 40, 0x2000)),
        };
        var episodes = order.Split(',').Select((name, index) => new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Name = name,
            EpisodeNumber = index + 1,
            Duration = 1800,
            IntroFingerprintEnd = 240,
        }).ToList();
        var ffmpeg = new StubFFmpegService
        {
            Fingerprints = (episode, _) => fingerprints[episode.Name],
            RangeBlackFrames = (_, _, _, _, _) => [new BlackFrame(100, 28, 0), new BlackFrame(100, 50, 1), new BlackFrame(100, 90, 2)],
        };
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var analyzer = new ChromaprintAnalyzer(
            NullLogger<ChromaprintAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempCacheService(),
            database,
            new PluginConfiguration
            {
                AnchorRecapToColdOpen = true,
                MaximumFingerprintPointDifferences = 0,
                MaximumTimeSkip = 0.2,
                InvertedIndexShift = 0,
                AdjustIntroBasedOnChapters = false,
                AdjustIntroBasedOnSilence = false,
                SnapToKeyframe = false,
            });

        await analyzer.AnalyzeMediaFiles(episodes, AnalysisMode.Recap, CancellationToken.None);

        var a = episodes.Single(episode => episode.Name == "A");
        Assert.Equal(EpisodeState.Analyzed, a.GetAnalyzed(AnalysisMode.Recap));
        var recap = Assert.Single(await database.GetSegmentsAsync(a.EpisodeId));
        Assert.Equal(28, TickConversions.ToSeconds(recap.StartTicks));
        Assert.Equal(90, TickConversions.ToSeconds(recap.EndTicks));

        // Sixty seconds of Chromaprint points unique to one episode, overlaid with runs shared
        // with other episodes at the given seconds.
        static uint[] Fingerprint(uint unique, params (double Start, double End, uint Shared)[] runs)
        {
            var points = Enumerable.Range(0, Position(60)).Select(i => unique + (uint)i).ToArray();
            foreach (var (start, end, shared) in runs)
            {
                for (var i = Position(start); i < Position(end); i++)
                {
                    points[i] = shared + (uint)(i - Position(start));
                }
            }

            return points;
        }

        static int Position(double seconds) => (int)Math.Round(seconds / ChromaprintConstants.SampleDuration);
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
