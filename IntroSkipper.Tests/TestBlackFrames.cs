// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TestBlackFrames
{
    [FactSkipFFmpegTests]
    public async Task TestBlackFrameDetection()
    {
        var range = 1e-5;

        var expected = new List<BlackFrame>();
        expected.AddRange(CreateFrameSequence(2, 3));
        expected.AddRange(CreateFrameSequence(5, 6));
        expected.AddRange(CreateFrameSequence(8, 9.96));

        var actual = await FfmpegTestHelpers.CreateFFmpegService().DetectBlackFramesAsync(FfmpegTestHelpers.QueueFile("video/rainbow.mp4"), new(0, 10), 85, 32, AnalysisMode.Introduction);

        for (var i = 0; i < expected.Count; i++)
        {
            var (e, a) = (expected[i], actual[i]);
            Assert.Equal(e.Percentage, a.Percentage);
            Assert.InRange(a.Time, e.Time - range, e.Time + range);
        }
    }

    [FactSkipFFmpegTests]
    public async Task TestSeekSampleKeyFrames()
    {
        var actual = await FfmpegTestHelpers.CreateFFmpegService().DetectKeyFramesAsync(
            FfmpegTestHelpers.QueueFile("video/seek-sample.mp4"),
            new(0, 8),
            AnalysisMode.Introduction);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], actual);
    }

    [FactSkipFFmpegTests]
    public async Task TestDetectKeyframeVisuals_ClipsScanToCreditsWindow()
    {
        // Real FFmpeg: -skip_frame nokey + -to does NOT reliably bound the scan (it emits keyframes
        // past the requested duration), so DetectKeyframeVisualsAsync must clip parsed visuals to the
        // window. credits.mp4 has keyframes every 10s; for window [5,35] (Start=5, Duration=30) the scan
        // must return only the in-window keyframes (seek-relative 5/15/25 = source 10/20/30), never the
        // leaked frames past 30s that would otherwise let credits be detected past CreditsFingerprintEnd.
        var episode = FfmpegTestHelpers.QueueFile("video/credits.mp4");
        episode.Duration = 330;
        episode.CreditsFingerprintStart = 5;
        episode.CreditsFingerprintEnd = 35;

        var visuals = await FfmpegTestHelpers.CreateFFmpegService().DetectKeyframeVisualsAsync(episode);

        Assert.NotEmpty(visuals);
        Assert.All(visuals, v => Assert.InRange(v.Time, 0, 30)); // clipped to range.Duration, no leak
        Assert.Equal(new[] { 5.0, 15.0, 25.0 }, Array.ConvertAll(visuals, v => v.Time));
        Assert.All(visuals, v => Assert.InRange(v.Entropy, 0, 1)); // real normalized entropy parsed
        Assert.All(visuals, v => Assert.True(v.Saturation >= 0)); // real SATAVG parsed
    }

    [Fact]
    public void TestParseBlackIntervals_LogOutput()
    {
        const string raw = """
            [blackdetect @ 0000000000000000] black_start:3.04 black_end:9.96 black_duration:6.92
            [blackdetect @ 0000000000000000] black_start:15 black_end:20.5 black_duration:5.5
            """;

        var intervals = FFmpegOutputParser.ParseBlackIntervals(raw);

        Assert.Equal(2, intervals.Length);
        Assert.Equal(new BlackInterval(3.04, 9.96), intervals[0]);
        Assert.Equal(new BlackInterval(15, 20.5), intervals[1]);
    }

    [Fact]
    public void TestParseBlackIntervals_IgnoresIncompleteOrInvalidIntervals()
    {
        const string raw = """
            [blackdetect @ 0000000000000000] black_start:3.04
            [blackdetect @ 0000000000000000] black_start:9 black_end:8 black_duration:1
            """;

        var intervals = FFmpegOutputParser.ParseBlackIntervals(raw);

        Assert.Empty(intervals);
    }

    [FactSkipFFmpegTests]
    public async Task TestEndCreditDetection()
    {
        // new strategy new range
        var range = 3;

        var analyzer = new BlackFrameAnalyzer(
            NullLogger<BlackFrameAnalyzer>.Instance,
            FfmpegTestHelpers.CreateFFmpegService(),
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            new PluginConfiguration());

        var episode = FfmpegTestHelpers.QueueFile("video/credits.mp4");
        episode.Duration = (int)new TimeSpan(0, 5, 30).TotalSeconds;

        var result = await analyzer.AnalyzeMediaFileAsync(episode, 240, 85, 32);
        Assert.NotNull(result);
        Assert.InRange(result.Start, 300 - range, 300 + range);
    }

    [Fact]
    public async Task TryAnalyzeChaptersAsync_ReturnsNullWhenBlackRunStartIsOutsideScanWindow()
    {
        var result = await RunChapterAnalysisAsync(
            blackRanges: [new TimeRange(2274, 2276), new TimeRange(2399, 2420)],
            chapterStarts: [2417.76],
            duration: 2444.6);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryAnalyzeChaptersAsync_AcceptsCreditsChapterWhenPreCreditsFadeExceedsFiveSeconds()
    {
        // Covers the accept path: a chapter marker is valid whenever it sits within
        // MaxChapterOffsetFromBlackRunStart of the start of its black run, independent of fade
        // length. The earlier act-break chapter also starts a black run and must not be
        // selected, locking in the rule that only the latest suitable chapter is ever
        // considered (#889).
        const double ActBreakChapterStart = 2274;
        const double ActBreakBlackRunEnd = 2276;
        const double PreCreditsFadeStart = 2394; // fade begins 6s before the credits chapter
        const double CreditsChapterStart = 2400;
        const double CreditsBlackRunEnd = 2420;
        const double EpisodeDuration = 2444.6;

        var result = await RunChapterAnalysisAsync(
            blackRanges: [new TimeRange(ActBreakChapterStart, ActBreakBlackRunEnd), new TimeRange(PreCreditsFadeStart, CreditsBlackRunEnd)],
            chapterStarts: [ActBreakChapterStart, CreditsChapterStart],
            duration: EpisodeDuration);

        Assert.NotNull(result);
        Assert.Equal(CreditsChapterStart, result.Start, 3);
        Assert.Equal(EpisodeDuration, result.End, 3);
    }

    [Fact]
    public async Task TryAnalyzeChaptersAsync_ReturnsNullWhenChapterCreditsExceedMaximumDuration()
    {
        // The chapter exceeds the default max credits duration (450s), so chapter analysis must fall back.
        var result = await RunChapterAnalysisAsync(
            blackRanges: [new TimeRange(2400, 2410)],
            chapterStarts: [2400],
            duration: 2900);

        Assert.Null(result);
    }

    [Fact]
    public void TestMergeScenesAcross20SecondGap()
    {
        // Two black-frame scenes whose raw endpoint timestamps differ by exactly 20s.
        // Since DetectCreditScenes compares scene.StartTime - current.EndTime, this should merge.
        // Gap uses 10 widely-spaced frames so merged density (40/50 = 80%) stays well above
        // the 50% threshold, isolating the MaximumTimeSkip boundary condition.
        var frames = new List<BlackFrame>();
        frames.AddRange(CreateDenseFrames(startTime: 0, endTime: 9.5, percentage: 95));
        for (var i = 0; i < 10; i++)
        {
            frames.Add(new BlackFrame(10, 10.0 + (i * 2.0), 20 + i));
        }

        frames.AddRange(CreateDenseFrames(startTime: 29.5, endTime: 39.0, percentage: 95, startFrame: 30));

        // minimum=85, sceneChange=96 (with floor=0 these are direct values)
        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 15);

        // The raw scene endpoint timestamps differ by exactly 20.0s: 29.5 - 9.5.
        // That is within MaximumTimeSkip, so the scenes should merge.
        Assert.Single(scenes);

        // StartTime == 0.0 also validates the transition-frame search: no frame reaches
        // the sceneChange threshold (96), so the start is not shifted forward.
        Assert.Equal(0.0, scenes[0].StartTime);
        Assert.Equal(39.0, scenes[0].EndTime);
    }

    [Fact]
    public void TestDoesNotMergeScenesAcross20Point5SecondGap()
    {
        // Two black-frame scenes whose raw endpoint timestamps differ by 20.5s.
        // Since DetectCreditScenes compares scene.StartTime - current.EndTime, this should not merge.
        List<BlackFrame> frames =
        [
            .. CreateDenseFrames(startTime: 0, endTime: 9.5, percentage: 95),
            .. CreateDenseFrames(startTime: 10, endTime: 29.5, percentage: 10),
            .. CreateDenseFrames(startTime: 30, endTime: 39.5, percentage: 95),
        ];

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 5);

        // The raw scene endpoint timestamps differ by 20.5s: 30.0 - 9.5.
        // That exceeds MaximumTimeSkip, so the scenes should stay separate.
        Assert.Equal(2, scenes.Count);
    }

    [Theory]
    [InlineData(90, 90, 89, 96)] // uniform 90% frames: floor capped at 30, not 90
    [InlineData(5, 80, 85, 95)]  // 1st percentile of 5%: floor stays 5, not capped
    public void TestNormalizeThreshold(int firstTwoPercentage, int otherPercentage, int expectedMinimum, int expectedSceneChange)
    {
        // minimum = (85 * (100 - floor) / 100) + floor; sceneChange = (95 * (100 - floor) / 100) + floor
        var frames = CreateFrames(100, i => i < 2 ? firstTwoPercentage : otherPercentage);

        var (minimum, sceneChange) = BlackFrameThresholdHelper.NormalizeThreshold(frames, 85);

        Assert.Equal(expectedMinimum, minimum);
        Assert.Equal(expectedSceneChange, sceneChange);
    }

    [Fact]
    public void TestDensityGating_AcceptsHighDensityScene()
    {
        // Simulate real credits: 100 keyframes, 80 are "black" (80% density)
        var frames = CreateFrames(100, i => i % 5 == 0 ? 30 : 90);

        var scenes = CreditSceneBuilder.DetectCreditScenes([.. frames], 85, 96, minimumDuration: 15);

        // With density gating at 50%, the scene should be accepted (80% density)
        Assert.NotEmpty(scenes);
    }

    [Fact]
    public void TestDetectCreditScenes_RepeatedLowDensityScenes_RejectedWithoutIntervalSupport()
    {
        // Repeated low-density clusters (~33% black keyframes) must NOT pass on keyframe evidence
        // alone. The static density floor rejects them here; genuine low-density credits are instead
        // rescued by blackdetect interval confirmation in CreditsBlackFrameAnalyzer, not by relaxing
        // this gate. This locks in the fix for the multi-scene false-positive path.
        List<BlackFrame> frames =
        [
            .. CreateFrames(60, LowDensityPercentage, startTime: 0, startFrame: 0),
            .. CreateFrames(60, LowDensityPercentage, startTime: 60, startFrame: 120),
            .. CreateFrames(60, LowDensityPercentage, startTime: 120, startFrame: 240),
        ];

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 15);

        Assert.Empty(scenes);
    }

    [Fact]
    public void TestRefineBoundary_NoPriorKeyframe_ReturnsOriginalStart()
    {
        // When the scene starts at the very first keyframe's time, there is no preceding keyframe.
        // FindBoundaryKeyframeTimes should return null.
        var frames = new List<BlackFrame>
        {
            new(95, 0.0, 0),
            new(95, 0.5, 1),
            new(95, 1.0, 2),
            new(95, 1.5, 3),
            new(95, 2.0, 4),
            new(95, 2.5, 5),
        };

        var scene = new CreditScene(0, 5, 0.0, 2.5);

        // Scene starts at the first keyframe — no preceding keyframe exists
        var result = CreditsBoundaryHelper.FindBoundaryKeyframeTimes(frames, scene);
        Assert.Null(result);
    }

    [Fact]
    public void TestRefineBoundary_HasPriorKeyframe_ReturnsBoundaryTimes()
    {
        // When there is a keyframe before the scene, return the boundary times.
        // The preceding keyframe is returned regardless of its black percentage.
        var frames = new List<BlackFrame>
        {
            new(20, 0.0, 0),   // non-black
            new(30, 0.5, 1),   // non-black — immediately precedes scene
            new(95, 1.0, 2),   // black — scene start
            new(95, 1.5, 3),
            new(95, 2.0, 4),
            new(95, 2.5, 5),
        };

        var scene = new CreditScene(2, 5, 1.0, 2.5);

        var result = CreditsBoundaryHelper.FindBoundaryKeyframeTimes(frames, scene);
        Assert.NotNull(result);
        Assert.Equal(0.5, result.Value.LastKeyframeTime);  // preceding keyframe at 0.5s
        Assert.Equal(1.0, result.Value.FirstBlackTime);    // scene start at 1.0s
    }

    [Fact]
    public void TestRefineBoundary_PrecedingKeyframeIsBlack_StillReturnsPrecedingKeyframe()
    {
        // On dark shows, the keyframe immediately before the scene may also have
        // percentage >= minimum. The method should still return it as the boundary,
        // not search further back for a "non-black" frame.
        var frames = new List<BlackFrame>
        {
            new(10, 0.0, 0),   // non-black (far back)
            new(90, 5.0, 1),   // black (but not credits)
            new(88, 10.0, 2),  // black (but not credits) — immediately precedes scene
            new(95, 15.0, 3),  // black — scene start
            new(95, 20.0, 4),
            new(95, 25.0, 5),
        };

        var scene = new CreditScene(3, 5, 15.0, 25.0);

        var result = CreditsBoundaryHelper.FindBoundaryKeyframeTimes(frames, scene);
        Assert.NotNull(result);
        // Old behavior would return 0.0 (last frame with percentage < 85).
        // New behavior returns 10.0 (immediately preceding keyframe).
        Assert.Equal(10.0, result.Value.LastKeyframeTime);
        Assert.Equal(15.0, result.Value.FirstBlackTime);
    }

    [Fact]
    public void TestDensityGating_DoesNotMergeAcrossLowDensityGap()
    {
        // Two dense black-frame segments separated by a long non-black gap.
        // Each segment passes density on its own, but the combined span does not.
        // The analyzer should keep them separate rather than merging into one low-density scene.
        List<BlackFrame> frames =
        [
            .. CreateDenseFrames(startTime: 0, endTime: 7.5, percentage: 95),
            .. CreateDenseFrames(startTime: 8, endTime: 26.5, percentage: 10),
            .. CreateDenseFrames(startTime: 27, endTime: 34.5, percentage: 95),
        ];

        // Gap between scenes: 27.0 - 7.5 = 19.5s (within MaximumTimeSkip of 20s)
        // Combined span after merge: 0-34.5s = 35s total, 32 black frames out of 70 total → ~46% density
        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 5);

        Assert.Equal(2, scenes.Count);
        Assert.Equal(0.0, scenes[0].StartTime);
        Assert.Equal(7.5, scenes[0].EndTime);
        Assert.Equal(27.0, scenes[1].StartTime);
        Assert.Equal(34.5, scenes[1].EndTime);
    }

    [Theory]
    [InlineData(2, 92, 92)]  // lower of the scene start frame percentage and sceneChange
    [InlineData(2, 99, 95)]  // capped at sceneChange
    [InlineData(99, 92, 95)] // scene start frame missing from the keyframe list (interval-derived): falls back to sceneChange
    public void TestSelectProbeMinimum(int sceneStartFrame, int startFramePercentage, int expected)
    {
        var frames = new List<BlackFrame>
        {
            new(20, 0.0, 0),
            new(30, 0.5, 1),
            new(startFramePercentage, 1.0, 2),
            new(95, 1.5, 3),
        };

        var scene = new CreditScene(sceneStartFrame, sceneStartFrame + 1, 1.0, 1.5);

        Assert.Equal(expected, CreditsBoundaryHelper.SelectProbeMinimum(frames, scene, sceneChange: 95));
    }

    [Theory]
    [InlineData(10.4, 30.0, 10.0, false)] // keyframe gap below the minimum probe window
    [InlineData(10.0, 20.0, 8.0, false)]  // even a full-window refinement cannot reach the minimum duration
    [InlineData(10.0, 24.0, 8.5, true)]   // meaningful window that can reach the minimum duration
    public void TestShouldRefineBoundary(double sceneStart, double sceneEnd, double lastKeyframeTime, bool expected)
    {
        var scene = new CreditScene(20, 40, sceneStart, sceneEnd);

        Assert.Equal(expected, CreditsBoundaryHelper.ShouldRefineBoundary(scene, lastKeyframeTime, minimumDuration: 15));
    }

    [Theory]
    [InlineData(0.0, null)] // probe hit at the preceding keyframe itself
    [InlineData(2.5, 12.5)] // inside the boundary window
    [InlineData(5.0, 15.0)] // exactly at the scene start (a no-op) is still accepted
    [InlineData(6.0, null)] // past the scene start
    public void TestTryRefineBoundaryTime(double probeTime, double? expected)
    {
        Assert.Equal(expected, CreditsBoundaryHelper.TryRefineBoundaryTime(probeTime, lastKeyframeTime: 10.0, sceneStartTime: 15.0));
    }

    [Fact]
    public async Task TestDetectCreditsAsync_EmptyScan_ReturnsNull()
    {
        var ffmpeg = CreditsScan([]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
        Assert.Equal(1, ffmpeg.CreditsScanCalls);
        Assert.Equal(0, ffmpeg.IntervalScanCalls);
        Assert.Equal(0, ffmpeg.RangeScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_SingleCleanScene_ReturnsOffsetSegment()
    {
        var ffmpeg = CreditsScan(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(100, result.Start);
        Assert.Equal(120, result.End);
        Assert.Equal(0, ffmpeg.IntervalScanCalls);
        Assert.Equal(0, ffmpeg.RangeScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_TooShortScene_ReturnsNull()
    {
        var ffmpeg = CreditsScan(CreateDenseFrames(startTime: 0, endTime: 10, percentage: 95));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_DarkLowDensityScene_ReturnsNull()
    {
        var ffmpeg = CreditsScan(CreateFrames(100, i => i % 5 == 0 ? 95 : 30));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_LowDensitySingleCandidateUsesIntervalSupport()
    {
        var ffmpeg = CreditsScan(
            CreateLowDensitySingleCandidateFrames(),
            intervals: [new BlackInterval(1, 49)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
        var intervalRange = Assert.IsType<TimeRange>(ffmpeg.LastIntervalRange);
        Assert.Equal(0, intervalRange.Start);
        Assert.Equal(64.5, intervalRange.End);
        Assert.True(intervalRange.End < episode.CreditsFingerprintEnd);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_LowDensitySingleCandidateWithoutIntervalSupportReturnsNull()
    {
        var ffmpeg = CreditsScan(CreateLowDensitySingleCandidateFrames());
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_StingerSplit_ReturnsFinalScene()
    {
        var ffmpeg = CreditsScan(CreateStingerSplitFrames());
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 1000);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(1090, result.Start);
        Assert.Equal(1120, result.End);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_ValidBlackFrameSceneSkipsIntervalPromotion()
    {
        var ffmpeg = CreditsScan(
            CreateStingerSplitFrames(),
            intervals: [new BlackInterval(5, 10)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 1000);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(1090, result.Start);
        Assert.Equal(1120, result.End);
        Assert.Equal(0, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_BlackIntervalsRecoverSparseKeyframeCredits()
    {
        BlackFrame[] frames =
        [
            new(15, 366.45, 36),
            new(96, 376.46, 37),
            new(96, 386.47, 38),
            new(98, 396.48, 39),
            new(99, 406.49, 40),
            new(20, 416.5, 41),
        ];
        var ffmpeg = CreditsScan(frames, intervals: [new BlackInterval(367.827, 376.002)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 2356.27);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.InRange(result.Start, 2724.096, 2724.098);
        Assert.InRange(result.End, 2762.759, 2762.761);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_SparseSingleSceneWithoutIntervalSupportIsStillReturned()
    {
        // A single scene that already clears the density and duration gates but is temporally sparse
        // triggers an opportunistic blackdetect probe. When that probe finds no supporting interval the
        // scene is kept, not rejected: sparsity drives optional refinement, it is not a trust gate. This
        // is the deliberate counterpart to the count==0 candidate path, which does require interval support.
        BlackFrame[] frames =
        [
            new(10, 0, 0),
            new(96, 10, 1),
            new(96, 20, 2),
            new(96, 30, 3),
            new(96, 40, 4),
            new(10, 50, 5),
        ];
        var ffmpeg = CreditsScan(frames);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(10, result.Start);
        Assert.Equal(40, result.End);

        // The opportunistic interval probe ran but returned nothing; the keyframe scene survives the miss.
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_BlackIntervalsExpandSingleShortScene()
    {
        BlackFrame[] frames =
        [
            new(96, 10, 10),
            new(96, 12, 11),
            new(96, 14, 12),
            new(96, 16, 13),
            new(96, 18, 14),
            new(96, 20, 15),
        ];
        var ffmpeg = CreditsScan(frames, intervals: [new BlackInterval(5, 19.8)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(105, result.Start);
        Assert.Equal(120, result.End);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public void TestDetectIntervalSupportedCreditScenes_UsesIntervalEndForDurationAndBounds()
    {
        BlackFrame[] frames =
        [
            new(96, 10, 10),
            new(96, 12, 12),
        ];

        var scenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(
            [.. frames],
            [new BlackInterval(5, 25)],
            minimum: 85,
            minimumDuration: 15);

        var scene = Assert.Single(scenes);
        Assert.Equal(5, scene.StartTime);
        Assert.Equal(25, scene.EndTime);
    }

    [Fact]
    public void TestDetectIntervalSupportedCreditScenes_AnchorsTailSupportToInterval()
    {
        var frames = new List<BlackFrame>();
        for (var time = 0; time <= 100; time += 10)
        {
            frames.Add(new BlackFrame(96, time, time));
        }

        var scenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(
            frames,
            [new BlackInterval(90, 120)],
            minimum: 85,
            minimumDuration: 15);

        var scene = Assert.Single(scenes);
        Assert.Equal(90, scene.StartTime);
        Assert.Equal(120, scene.EndTime);
        Assert.Equal(90, scene.StartFrame);
        Assert.Equal(100, scene.EndFrame);
    }

    [Fact]
    public void TestDetectIntervalSupportedCreditScenes_PrefersLongerOverlappingInterval()
    {
        BlackFrame[] frames =
        [
            new(96, 10, 10),
            new(96, 12, 11),
        ];

        // The first overlapping interval is too short to satisfy the minimum duration; a later, longer
        // overlapping interval must still be used instead of rejecting the candidate.
        var scenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(
            [.. frames],
            [new BlackInterval(9, 13), new BlackInterval(9, 40)],
            minimum: 85,
            minimumDuration: 15);

        var scene = Assert.Single(scenes);
        Assert.Equal(9, scene.StartTime);
        Assert.Equal(40, scene.EndTime);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_BlackIntervalsWithoutBlackframeSupportReturnNull()
    {
        BlackFrame[] frames =
        [
            new(15, 366.45, 36),
            new(20, 376.46, 37),
            new(18, 386.47, 38),
            new(22, 396.48, 39),
        ];
        var ffmpeg = CreditsScan(frames, intervals: [new BlackInterval(367.827, 376.002)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 2356.27);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
        Assert.Equal(0, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public void TestCreditSceneMetrics_DetectsSparseScenesFromAverageBlackFrameGap()
    {
        var scene = new CreditScene(1, 4, 10, 40);
        BlackFrame[] frames =
        [
            new(96, 10, 1),
            new(97, 20, 2),
            new(98, 30, 3),
            new(99, 40, 4),
        ];

        var metrics = CreditSceneMetricsCalculator.Calculate(frames, scene, minimum: 85);

        Assert.Equal(4, metrics.BlackFrameCount);
        Assert.True(metrics.MeetsDensity(CreditDetectionPolicy.DefaultMinimumBlackFrameDensity));
        Assert.True(metrics.IsSparse(scene, minimumDuration: 15));
    }

    [Fact]
    public void TestIntervalProbeRanges_MergesOverlappingPaddedRanges()
    {
        var ranges = CreditsBlackFrameAnalyzer.BuildIntervalProbeRanges(
            [
                new CreditScene(10, 20, 100, 120),
                new CreditScene(21, 30, 130, 150),
                new CreditScene(80, 90, 300, 330),
            ],
            minimumDuration: 15,
            fingerprintStart: 1000,
            fingerprintEnd: 1400);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(1085, ranges[0].Start);
        Assert.Equal(1165, ranges[0].End);
        Assert.Equal(1285, ranges[1].Start);
        Assert.Equal(1345, ranges[1].End);
    }

    [Theory]
    [MemberData(nameof(CandidateRankingCases))]
    public void TestRankCreditCandidates_SelectsExpectedScene(CreditScene[] scenes, BlackInterval[] intervals, int expectedIndex)
    {
        var selected = CreditsBlackFrameAnalyzer.RankCreditCandidates(scenes, intervals)[0];

        Assert.Equal(scenes[expectedIndex], selected);
    }

    public static IEnumerable<object[]> CandidateRankingCases()
    {
        CreditScene[] twoScenes =
        [
            new(400, 520, 200, 260),
            new(620, 700, 310, 350),
        ];

        // No interval evidence: the latest scene wins.
        yield return [twoScenes, Array.Empty<BlackInterval>(), 1];

        // An interval overlapping the earlier scene beyond the minimum promotes it over the later scene.
        yield return [twoScenes, new[] { new BlackInterval(205, 246) }, 0];

        // Overlap shorter than MinimumIntervalOverlapSeconds (0.25s) is not support: the latest scene wins.
        yield return [twoScenes, new[] { new BlackInterval(259.9, 280) }, 1];

        // An interval supporting the later scene keeps the latest scene selected.
        yield return [twoScenes, new[] { new BlackInterval(315, 360) }, 1];

        CreditScene[] threeScenes =
        [
            new(100, 200, 50, 90),
            new(300, 400, 150, 190),
            new(500, 600, 250, 290),
        ];

        // Among supported scenes the latest supported one wins, ahead of an unsupported later scene.
        yield return [threeScenes, new[] { new BlackInterval(55, 95), new BlackInterval(155, 195) }, 1];
    }

    [Fact]
    public async Task TestDetectCreditsAsync_RefinesBoundaryByDefault()
    {
        List<BlackFrame> frames =
        [
            .. CreateDenseFrames(startTime: 0, endTime: 8, percentage: 30),
            .. CreateDenseFrames(startTime: 10, endTime: 30, percentage: 95, startFrame: 20),
        ];

        var ffmpeg = CreditsScan([.. frames], [new BlackFrame(95, 1.25, 0)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(109.25, result.Start);
        Assert.Equal(130, result.End);
        Assert.Equal(1, ffmpeg.RangeScanCalls);
        var probe = Assert.NotNull(ffmpeg.LastRangeScan);
        Assert.Equal(108, probe.Range.Start);
        Assert.Equal(110, probe.Range.End);
        Assert.Equal(95, probe.Minimum);
        Assert.Equal(32, probe.Threshold);
        Assert.Equal(AnalysisMode.Credits, probe.Mode);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_RefinesSubMinimumFinalSceneBeforeSelectingEarlierScene()
    {
        List<BlackFrame> frames =
        [
            .. CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95),
            .. CreateDenseFrames(startTime: 20.5, endTime: 58, percentage: 30),
            .. CreateDenseFrames(startTime: 60, endTime: 74, percentage: 95, startFrame: 120),
        ];

        var ffmpeg = CreditsScan([.. frames], [new BlackFrame(95, 0.5, 0)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(158.5, result.Start);
        Assert.Equal(174, result.End);
        Assert.Equal(1, ffmpeg.RangeScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_DisabledBoundaryRefinement_UsesKeyframeStart()
    {
        List<BlackFrame> frames =
        [
            .. CreateDenseFrames(startTime: 0, endTime: 8, percentage: 30),
            .. CreateDenseFrames(startTime: 10, endTime: 30, percentage: 95, startFrame: 20),
        ];

        var ffmpeg = CreditsScan([.. frames], [new BlackFrame(95, 1.25, 0)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(110, result.Start);
        Assert.Equal(130, result.End);
        Assert.Equal(0, ffmpeg.RangeScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_DisabledRefinement_DoesNotSuppressIntervalFallback()
    {
        List<BlackFrame> frames =
        [
            .. CreateDenseFrames(startTime: 0, endTime: 8, percentage: 30),
            .. CreateDenseFrames(startTime: 14, endTime: 24, percentage: 95, startFrame: 40),
        ];

        // The only keyframe scene is too short on its own and could reach the minimum duration only via
        // boundary refinement. With refinement disabled it must not be admitted, so the interval fallback
        // can still recover the credits instead of the analyzer returning null.
        var ffmpeg = CreditsScan([.. frames], intervals: [new BlackInterval(8, 24)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
        Assert.Equal(108, result.Start);
        Assert.Equal(124, result.End);
    }

    [Fact]
    public async Task TestAnalyzeMediaFiles_RejectsNonCreditsMode()
    {
        var analyzer = CreateCreditsBlackFrameAnalyzer(CreditsScan([]));

        await Assert.ThrowsAsync<NotImplementedException>(
            () => analyzer.AnalyzeMediaFiles([], AnalysisMode.Introduction, CancellationToken.None));
    }

    [Fact]
    public async Task TestAnalyzeMediaFiles_SkipsAlreadyAnalyzedEpisodes()
    {
        var episode = CreateQueuedCreditsEpisode();
        episode.SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        var ffmpeg = CreditsScan(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);

        var result = await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Credits, CancellationToken.None);

        Assert.Same(episode, result[0]);
        Assert.Equal(0, ffmpeg.CreditsScanCalls);
    }

    [Fact]
    public async Task TestAnalyzeMediaFiles_CancellationBeforeEpisode_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var episode = CreateQueuedCreditsEpisode();
        var ffmpeg = CreditsScan(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Credits, cts.Token));
        Assert.Equal(0, ffmpeg.CreditsScanCalls);
    }

    [Fact]
    public async Task TestAnalyzeMediaFiles_DetectionException_Continues()
    {
        var episode = CreateQueuedCreditsEpisode();
        var ffmpeg = new StubFFmpegService
        {
            CreditsBlackFrames = (_, _) => throw new InvalidOperationException("test failure"),
        };
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());

        var result = await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Credits, CancellationToken.None);

        Assert.Same(episode, result[0]);
        Assert.Equal(EpisodeState.AnalysisFailed, episode.GetAnalyzed(AnalysisMode.Credits));
        Assert.True(episode.NeedsAnalysis(AnalysisMode.Credits));
        Assert.Equal(1, ffmpeg.CreditsScanCalls);
    }

    // ── Non-black (entropy/saturation) credit fallback ───────────────────

    [Fact]
    public void TestParseKeyframeVisuals_ParsesEntropyAndSaturation()
    {
        const string raw = """
            [Parsed_metadata_2 @ 0x0] frame:0    pts:0       pts_time:0
            [Parsed_metadata_2 @ 0x0] lavfi.entropy.normalized_entropy.normal.Y=0.531285
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=108.199
            [Parsed_metadata_2 @ 0x0] frame:1    pts:20480   pts_time:2
            [Parsed_metadata_2 @ 0x0] lavfi.entropy.normalized_entropy.normal.Y=0.000000
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=33
            """;

        var visuals = FFmpegOutputParser.ParseKeyframeVisuals(raw);

        Assert.Equal(2, visuals.Length);
        Assert.Equal(new KeyframeVisual(0.0, 0.531285, 108.199), visuals[0]);
        Assert.Equal(new KeyframeVisual(2.0, 0.0, 33.0), visuals[1]);
    }

    [Fact]
    public void TestParseKeyframeVisuals_UsesLumaPlaneAndSkipsBlocksWithoutEntropy()
    {
        // U/V entropy lines must not be mistaken for the luma plane, and a trailing block with no
        // entropy metadata (e.g. truncated output) must be dropped rather than emitted as zeros.
        const string raw = """
            [Parsed_metadata_3 @ 0x0] frame:0 pts:0 pts_time:5
            [Parsed_metadata_3 @ 0x0] lavfi.entropy.normalized_entropy.normal.Y=0.120000
            [Parsed_metadata_3 @ 0x0] lavfi.entropy.normalized_entropy.normal.U=0.400000
            [Parsed_metadata_3 @ 0x0] lavfi.entropy.normalized_entropy.normal.V=0.410000
            [Parsed_metadata_3 @ 0x0] lavfi.signalstats.SATAVG=12.5
            [Parsed_metadata_3 @ 0x0] frame:1 pts:1 pts_time:7
            """;

        var visual = Assert.Single(FFmpegOutputParser.ParseKeyframeVisuals(raw));

        Assert.Equal(5.0, visual.Time);
        Assert.Equal(0.12, visual.Entropy);
        Assert.Equal(12.5, visual.Saturation);
    }

    [Fact]
    public void TestParseKeyframeVisuals_SkipsBlocksWithoutSaturation()
    {
        // A trailing block truncated before lavfi.signalstats.SATAVG must be dropped rather than
        // emitted with the default saturation 0, which would otherwise pass the low-saturation
        // credit-card gate (0 < SaturationCreditMaximum) and fabricate a false card.
        const string raw = """
            [Parsed_metadata_2 @ 0x0] frame:0 pts:0 pts_time:5
            [Parsed_metadata_2 @ 0x0] lavfi.entropy.normalized_entropy.normal.Y=0.120000
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=12.5
            [Parsed_metadata_2 @ 0x0] frame:1 pts:1 pts_time:7
            [Parsed_metadata_2 @ 0x0] lavfi.entropy.normalized_entropy.normal.Y=0.050000
            """;

        var visual = Assert.Single(FFmpegOutputParser.ParseKeyframeVisuals(raw));

        Assert.Equal(5.0, visual.Time);
        Assert.Equal(0.12, visual.Entropy);
        Assert.Equal(12.5, visual.Saturation);
    }

    [Fact]
    public void TestParseKeyframeVisuals_ParsesExponentNotation()
    {
        // Defensive: stock FFmpeg emits decimal here, but if a build ever emits exponent form the whole
        // numeric token must be parsed, not truncated at the mantissa (which would record 1s instead of
        // ~0s and feed corrupt values into detection and the cache).
        const string raw = """
            [Parsed_metadata_2 @ 0x0] frame:0 pts:0 pts_time:1e-05
            [Parsed_metadata_2 @ 0x0] lavfi.entropy.normalized_entropy.normal.Y=1.5e-06
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=3.2e+01
            [Parsed_metadata_2 @ 0x0] frame:1 pts:1 pts_time:2
            """;

        var visual = Assert.Single(FFmpegOutputParser.ParseKeyframeVisuals(raw));

        Assert.Equal(1e-05, visual.Time);
        Assert.Equal(1.5e-06, visual.Entropy);
        Assert.Equal(32.0, visual.Saturation);
    }

    [Theory]
    [InlineData(0.12, 30.0, true)] // uniform, muted background -> credit card
    [InlineData(0.349, 95.0, true)] // just inside both exclusive maxima -> credit card
    [InlineData(0.35, 30.0, false)] // entropy at the exclusive max -> not a card
    [InlineData(0.55, 30.0, false)] // busy/high-entropy content -> not a card
    [InlineData(0.12, 96.0, false)] // saturation at the exclusive max -> not a card
    [InlineData(0.12, 200.0, false)] // vivid saturated colour -> not a card
    public void TestIsCreditCardKeyframe(double entropy, double saturation, bool expected)
    {
        Assert.Equal(expected, CreditEntropyFallback.IsCreditCardKeyframe(new KeyframeVisual(0, entropy, saturation)));
    }

    // Each row: keyframe visuals, minimum credit duration, expected (Start, End) or null.
    public static TheoryData<KeyframeVisual[], int, (double Start, double End)?> EntropyFallbackCases => new()
    {
        // Over-extension: dense credits 0-20, periodic isolated tail cards every 8s -> trim to 20.
        { Seq(58, 2, (0, 20), (30, 30), (38, 38), (46, 46), (54, 54)), 15, (0, 20) },

        // Leading over-extension: an isolated pre-credit card bridges into a dense block (4s GOP)
        // -> start anchored to the dense block, not the stray pre-card.
        { Seq(88, 4, (36, 36), (52, 80)), 15, (52, 80) },

        // Clean dense card run -> unchanged.
        { Seq(54, 2, (30, 54)), 15, (30, 54) },

        // Mid-body ident interlude (6s of non-card bracketed by dense cards) -> preserved.
        { Seq(60, 2, (0, 28), (36, 60)), 15, (0, 60) },

        // Interlude near the end (cards resume densely after) -> preserved.
        { Seq(60, 2, (0, 48), (56, 60)), 15, (0, 60) },

        // Sparse all-card credits (8s GOP) -> kept (100% density, trailing gap within scaled trim).
        { Seq(40, 8, (0, 40)), 15, (0, 40) },

        // Uniform sparse long-GOP credits (12s cadence) -> kept; the trim keys off the run's own
        // cadence, so an all-card run is never discarded even when its gap exceeds the capped bridge.
        { Seq(48, 12, (0, 48)), 15, (0, 48) },

        // All-card run whose 21s cadence exceeds the fixed bridge, with nothing non-card between
        // -> stays one run instead of splitting into one-frame runs that each miss the minimum.
        { Seq(63, 21, (0, 63)), 60, (0, 63) },

        // Two real runs separated by a long gap -> latest selected.
        { Seq(80, 2, (0, 20), (60, 80)), 15, (60, 80) },

        // An earlier dense run (0-20) must not capture or extend into a later long-GOP credit run
        // (60-96, 12s cadence): real content separates the groups, so the latest run is returned.
        { [.. Cards(0, 20, 2), .. Busy(22, 58, 2), .. Cards(60, 96, 12)], 15, (60, 96) },

        // Dense non-card content, then static cards on a 12s-keyframe source: grouping must key off the
        // card cadence, not the content cadence, or every 12s card gap splits the run.
        { [.. Busy(0, 58, 2), .. Cards(60, 96, 12)], 15, (60, 96) },

        // A substantial dense body followed by isolated cards every 8s out to the window edge -> the
        // trim anchors to the dense-body cadence and cuts the sparse tail back to the real block.
        { [.. Cards(0, 20, 2), .. Cards(28, 196, 8)], 15, (0, 20) },

        // Sparse isolated cards bridged across busy 2s content (brief dense head, then a lone card
        // every 8s) -> rejected by the card-density floor: most keyframes in the span are busy
        // content, so this reads as normal content with occasional static shots, not a card sequence.
        { Seq(54, 2, (0, 6), (14, 14), (22, 22), (30, 30), (38, 38), (46, 46), (54, 54)), 15, null },

        // Two card-like keyframes 18s apart with busy keyframes between them -> not credits.
        { Seq(18, 2, (0, 0), (18, 18)), 15, null },

        // Final card spaced just within cadence (4s) -> kept, not over-trimmed.
        { Seq(44, 2, (0, 40), (44, 44)), 15, (0, 44) },

        // Only 10s of card -> below the minimum duration.
        { Seq(40, 2, (30, 40)), 15, null },

        // Dark (low luma) but detailed content is high entropy, like a night scene -> not a card.
        { [.. Times(0, 58, 2).Select(t => new KeyframeVisual(t, 0.63, 50))], 15, null },

        // Uniform but vividly saturated frames are excluded on purpose (see CreditEntropyFallback).
        { CreateCardCreditVisuals(cardStart: 0, cardEnd: 20, cardSaturation: 200), 15, null },

        // All busy content -> null.
        { Seq(60, 2), 15, null },
    };

    [Theory]
    [MemberData(nameof(EntropyFallbackCases))]
    public void TestCreditEntropyFallback_FindCreditRange(KeyframeVisual[] visuals, int minimumDuration, (double Start, double End)? expected)
    {
        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration);

        if (expected is null)
        {
            Assert.Null(range);
            return;
        }

        Assert.NotNull(range);
        Assert.Equal(expected.Value.Start, range.Start);
        Assert.Equal(expected.Value.End, range.End);
    }

    [Theory]
    [InlineData(false, true)]  // frames present but none black: the black path finds no scene, so the fallback runs
    [InlineData(true, true)]   // empty black scan: the Count == 0 branch must also reach the fallback
    [InlineData(false, false)] // fallback disabled: never scanned
    [InlineData(true, false)]  // fallback disabled on the empty-scan branch: never scanned
    public async Task TestDetectCreditsAsync_NonBlackCreditsFallback(bool emptyBlackScan, bool detectNonBlackCredits)
    {
        var ffmpeg = CreditsScan(
            emptyBlackScan ? [] : CreateDenseFrames(startTime: 0, endTime: 54, percentage: 0),
            keyframeVisuals: CreateCardCreditVisuals(cardStart: 30, cardEnd: 54));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg, new PluginConfiguration { DetectNonBlackCredits = detectNonBlackCredits });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Equal(0, ffmpeg.IntervalScanCalls);
        if (!detectNonBlackCredits)
        {
            Assert.Null(result);
            Assert.Equal(0, ffmpeg.VisualScanCalls);
            return;
        }

        Assert.NotNull(result);
        Assert.Equal(130, result.Start);
        Assert.Equal(154, result.End);
        Assert.Equal(1, ffmpeg.VisualScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_IntervalMissThenFallback_RecoversNonBlackCredits()
    {
        // Low-density black candidates trigger interval confirmation; with no supporting intervals the
        // black path still finds no scene, so the analyzer must fall through to the non-black fallback.
        var ffmpeg = CreditsScan(
            CreateLowDensitySingleCandidateFrames(),
            intervals: [],
            keyframeVisuals: CreateCardCreditVisuals(cardStart: 30, cardEnd: 54));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(130, result.Start);
        Assert.Equal(154, result.End);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
        Assert.Equal(1, ffmpeg.VisualScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_BlackCreditsPresent_DoesNotRunFallback()
    {
        // A valid black-frame scene is found, so the frame-accurate black path must win and the
        // (keyframe-granular) entropy fallback must never be scanned.
        var ffmpeg = CreditsScan(
            CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95),
            keyframeVisuals: CreateCardCreditVisuals(cardStart: 0, cardEnd: 20));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(100, result.Start);
        Assert.Equal(0, ffmpeg.VisualScanCalls);
    }

    // ── Fingerprint-based integration tests ──────────────────────────────

    [Fact]
    public void TestFingerprint_Alt3_CleanCredits_SingleScene()
    {
        // alt-3: fewest frames (211), clean credits, no transition-frame shift.
        var frames = ParseFingerprintFile("blackframe-alt-3");
        var (minimum, sceneChange) = BlackFrameThresholdHelper.NormalizeThreshold(frames, 85);

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, minimum, sceneChange, minimumDuration: 15);

        // Single credit block, no transition-frame shift (no frame reaches sceneChange=95)
        Assert.Single(scenes);
        Assert.Equal(516.012, scenes[0].StartTime);
        Assert.Equal(584.33, scenes[0].EndTime);
    }

    [Fact]
    public void TestFingerprint_Alt4_DarkShow_TransitionFrameShift()
    {
        // alt-4: dark show where 31% of frames are >=85% pblack.
        // Transition-frame search shifts the last scene's start forward to skip dark-but-not-credits content.
        var frames = ParseFingerprintFile("blackframe-alt-4");
        var (minimum, sceneChange) = BlackFrameThresholdHelper.NormalizeThreshold(frames, 85);

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, minimum, sceneChange, minimumDuration: 15);

        Assert.True(scenes.Count >= 4);

        // The real credits are the last scene (backward iteration would pick this first).
        // Before transition-frame search: start=422.843s
        // After: first frame >= sceneChange (95) shifts start to 463.425s
        var credits = scenes[^1];
        Assert.Equal(463.425, credits.StartTime);
        Assert.Equal(558.479, credits.EndTime);
    }

    [Fact]
    public void TestFingerprint_Alt5_MidCreditScene_TwoSeparateBlocks()
    {
        // alt-5: mid-credit stinger creates two separate credit blocks. The non-zero
        // 1st-percentile floor (25) scales the thresholds to minimum=88, sceneChange=96.
        var frames = ParseFingerprintFile("blackframe-alt-5");
        var (minimum, sceneChange) = BlackFrameThresholdHelper.NormalizeThreshold(frames, 85);

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, minimum, sceneChange, minimumDuration: 15);

        // Two blocks separated by 88s stinger gap (725.12 - 637.12 = 88 >> MaximumTimeSkip of 20).
        // They must NOT merge.
        Assert.Equal(2, scenes.Count);

        // First block (pre-stinger credits)
        Assert.Equal(609.328, scenes[0].StartTime);
        Assert.Equal(637.12, scenes[0].EndTime);

        // Second block (post-stinger credits) — backward iteration picks this one.
        Assert.Equal(725.12, scenes[1].StartTime);
        Assert.Equal(853.12, scenes[1].EndTime);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a raw FFmpeg blackframe filter output file into a list of <see cref="BlackFrame"/> records.
    /// Delegates to the production <see cref="FFmpegOutputParser.ParseBlackFrame"/> parser to avoid
    /// regex/format drift between tests and implementation.
    /// </summary>
    private static List<BlackFrame> ParseFingerprintFile(string filename)
    {
        var path = Path.Combine("..", "..", "..", "fingerprints", filename);
        var raw = File.ReadAllText(path);
        return [.. FFmpegOutputParser.ParseBlackFrames(raw)];
    }

    /// <summary>
    /// Runs chapter-marker credit detection against a black-frame scan that reports black only inside
    /// <paramref name="blackRanges"/>, with credits fingerprinting starting at 2000s.
    /// </summary>
    private static async Task<Segment?> RunChapterAnalysisAsync(TimeRange[] blackRanges, double[] chapterStarts, double duration)
    {
        var ffmpeg = new StubFFmpegService
        {
            RangeBlackFrames = (_, range, _, _, _) => blackRanges.Any(r => range.Start >= r.Start && range.Start < r.End)
                ? [new BlackFrame(95, 0, 0)]
                : [],
        };
        var analyzer = new BlackFrameAnalyzer(
            NullLogger<BlackFrameAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            new PluginConfiguration());

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(
            Plugin.Instance!,
            "_chapterRepository",
            ChapterManagerStub.Create([.. chapterStarts.Select(start => new ChapterInfo { StartPositionTicks = TimeSpan.FromSeconds(start).Ticks })]));
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Duration = duration,
            CreditsFingerprintStart = 2000,
        };

        return await analyzer.TryAnalyzeChaptersAsync(episode, 85, 28, CancellationToken.None);
    }

    private static QueuedEpisode CreateQueuedCreditsEpisode(double creditsFingerprintStart = 0)
    {
        return new()
        {
            EpisodeId = Guid.NewGuid(),
            Name = "episode.mkv",
            Path = "episode.mkv",
            Duration = creditsFingerprintStart + 1800,
            CreditsFingerprintStart = creditsFingerprintStart,
            CreditsFingerprintEnd = creditsFingerprintStart + 1800,
        };
    }

    private static CreditsBlackFrameAnalyzer CreateCreditsBlackFrameAnalyzer(IFFmpegService ffmpegService, PluginConfiguration? configuration = null)
    {
        return new(NullLogger<CreditsBlackFrameAnalyzer>.Instance, ffmpegService, DatabaseTestHelpers.CreateTempSegmentDatabase(), configuration ?? new PluginConfiguration());
    }

    /// <summary>
    /// Creates a stub whose keyframe credits scan returns <paramref name="creditsFrames"/>, whose
    /// range probes return <paramref name="probeFrames"/>, and whose blackdetect and keyframe-visual
    /// scans return <paramref name="intervals"/> and <paramref name="keyframeVisuals"/>.
    /// </summary>
    private static StubFFmpegService CreditsScan(
        BlackFrame[] creditsFrames,
        BlackFrame[]? probeFrames = null,
        BlackInterval[]? intervals = null,
        KeyframeVisual[]? keyframeVisuals = null) => new()
        {
            CreditsBlackFrames = (_, _) => creditsFrames,
            RangeBlackFrames = (_, _, _, _, _) => probeFrames ?? [],
            BlackIntervals = (_, _, _, _) => intervals ?? [],
            KeyframeVisuals = _ => keyframeVisuals ?? [],
        };

    private static KeyframeVisual[] CreateCardCreditVisuals(
        double cardStart,
        double cardEnd,
        double cardEntropy = 0.15,
        double cardSaturation = 32,
        double contentEntropy = 0.53,
        double contentSaturation = 108)
    {
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time < cardStart; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, contentEntropy, contentSaturation));
        }

        for (var time = cardStart; time <= cardEnd; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, cardEntropy, cardSaturation));
        }

        return [.. visuals];
    }

    private static IEnumerable<double> Times(double from, double to, double step)
    {
        for (var t = from; t <= to + 1e-9; t += step)
        {
            yield return t;
        }
    }

    private static IEnumerable<KeyframeVisual> Cards(double from, double to, double step)
        => Times(from, to, step).Select(t => new KeyframeVisual(t, 0.12, 30));

    private static IEnumerable<KeyframeVisual> Busy(double from, double to, double step)
        => Times(from, to, step).Select(t => new KeyframeVisual(t, 0.55, 108));

    /// <summary>
    /// Keyframes every <paramref name="step"/> seconds from 0 to <paramref name="end"/>; those inside
    /// any of the <paramref name="cards"/> spans (inclusive) are credit cards, the rest busy content.
    /// </summary>
    private static KeyframeVisual[] Seq(double end, double step, params (double From, double To)[] cards)
        => [.. Times(0, end, step).Select(t => cards.Any(c => t >= c.From - 1e-9 && t <= c.To + 1e-9)
            ? new KeyframeVisual(t, 0.12, 30)
            : new KeyframeVisual(t, 0.55, 108))];

    private static BlackFrame[] CreateStingerSplitFrames() =>
    [
        .. CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95),
        .. CreateDenseFrames(startTime: 20.5, endTime: 89.5, percentage: 30, startFrame: 41),
        .. CreateDenseFrames(startTime: 90, endTime: 120, percentage: 95, startFrame: 180),
    ];

    private static int LowDensityPercentage(int i) => i % 3 == 0 ? 90 : 30;

    private static BlackFrame[] CreateLowDensitySingleCandidateFrames() => CreateFrames(100, LowDensityPercentage);

    /// <summary>
    /// Keyframes 0.5s apart with a per-index black percentage.
    /// </summary>
    private static BlackFrame[] CreateFrames(int count, Func<int, int> percentage, double startTime = 0, int startFrame = 0)
        => [.. Enumerable.Range(0, count).Select(i => new BlackFrame(percentage(i), startTime + (i * 0.5), startFrame + i))];

    /// <summary>
    /// Keyframes 0.5s apart from <paramref name="startTime"/> to <paramref name="endTime"/> (inclusive)
    /// with one black percentage; frame numbers default to twice the start time.
    /// </summary>
    private static BlackFrame[] CreateDenseFrames(double startTime, double endTime, int percentage, int? startFrame = null)
    {
        var frames = new List<BlackFrame>();
        var frame = startFrame ?? (int)(startTime * 2);
        for (var time = startTime; time <= endTime; time += 0.5)
        {
            frames.Add(new BlackFrame(percentage, time, frame++));
        }

        return [.. frames];
    }

    private static BlackFrame[] CreateFrameSequence(double start, double end)
    {
        var frames = new List<BlackFrame>();

        for (var i = start; i < end; i += 0.04)
        {
            frames.Add(new(100, i, 0));
        }

        return [.. frames];
    }
}
