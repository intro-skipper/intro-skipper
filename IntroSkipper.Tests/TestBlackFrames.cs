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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using MediaBrowser.Controller.Chapters;
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

        var actual = await CreateFFmpegService().DetectBlackFramesAsync(QueueFile("rainbow.mp4"), new(0, 10), 85, 32, AnalysisMode.Introduction);

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
        var actual = await CreateFFmpegService().DetectKeyFramesAsync(
            QueueFile("seek-sample.mp4"),
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
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Name = "credits.mp4",
            Path = "../../../video/credits.mp4",
            Duration = 330,
            CreditsFingerprintStart = 5,
            CreditsFingerprintEnd = 35,
        };

        var visuals = await CreateFFmpegService().DetectKeyframeVisualsAsync(episode);

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

        var analyzer = CreateBlackFrameAnalyzer();

        var episode = QueueFile("credits.mp4");
        episode.Duration = (int)new TimeSpan(0, 5, 30).TotalSeconds;

        var result = await analyzer.AnalyzeMediaFileAsync(episode, 240, 85, 32);
        Assert.NotNull(result);
        Assert.InRange(result.Start, 300 - range, 300 + range);
    }

    [Fact]
    public async Task TryAnalyzeChaptersAsync_ReturnsNullWhenBlackRunStartIsOutsideScanWindow()
    {
        var ffmpeg = new RangeBasedBlackFrameService(
        [
            new TimeRange(2274, 2276),
            new TimeRange(2399, 2420)
        ]);
        var analyzer = new BlackFrameAnalyzer(
            NullLogger<BlackFrameAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPrivateField(plugin, "_chapterRepository", ChapterManagerProxy.Create(
        [
            CreateChapterInfo(2417.76)
        ]));
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Duration = 2444.6,
            CreditsFingerprintStart = 2000,
        };

        var result = await analyzer.TryAnalyzeChaptersAsync(episode, 85, 28, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryAnalyzeChaptersAsync_ReturnsNullWhenChapterCreditsExceedMaximumDuration()
    {
        var ffmpeg = new RangeBasedBlackFrameService(
        [
            new TimeRange(2400, 2410)
        ]);
        var analyzer = new BlackFrameAnalyzer(
            NullLogger<BlackFrameAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        // The chapter exceeds the default max credits duration (450s), so chapter analysis must fall back.
        EntrypointTestHelpers.SetPrivateField(plugin, "_chapterRepository", ChapterManagerProxy.Create(
        [
            CreateChapterInfo(2400)
        ]));
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Duration = 2900,
            CreditsFingerprintStart = 2000,
        };

        var result = await analyzer.TryAnalyzeChaptersAsync(episode, 85, 28, CancellationToken.None);

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

        // First cluster: frames 0-19 at times 0-9.5s, all 95% black
        for (var i = 0; i < 20; i++)
        {
            frames.Add(new BlackFrame(95, i * 0.5, i));
        }

        // Gap: 10 frames at times 10-28s, all 10% black (non-black), spaced 2s apart
        for (var i = 0; i < 10; i++)
        {
            frames.Add(new BlackFrame(10, 10.0 + (i * 2.0), 20 + i));
        }

        // Second cluster: frames 30-49 at times 29.5-39.0s, all 95% black
        for (var i = 0; i < 20; i++)
        {
            frames.Add(new BlackFrame(95, 29.5 + (i * 0.5), 30 + i));
        }

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
        var frames = new List<BlackFrame>();

        // First cluster: frames 0-19 at times 0-9.5s, all 95% black
        for (var i = 0; i < 20; i++)
        {
            frames.Add(new BlackFrame(95, i * 0.5, i));
        }

        // Gap: frames 20-59 at times 10-29.5s, all 10% black (non-black)
        for (var i = 20; i < 60; i++)
        {
            frames.Add(new BlackFrame(10, i * 0.5, i));
        }

        // Second cluster: frames 60-79 at times 30.0-39.5s, all 95% black
        for (var i = 60; i < 80; i++)
        {
            frames.Add(new BlackFrame(95, i * 0.5, i));
        }

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 5);

        // The raw scene endpoint timestamps differ by 20.5s: 30.0 - 9.5.
        // That exceeds MaximumTimeSkip, so the scenes should stay separate.
        Assert.Equal(2, scenes.Count);
    }

    [Fact]
    public void TestNormalizeThreshold_UniformFrames()
    {
        // All frames at 90% black — floor should be 30% (capped), not 90%
        var frames = new List<BlackFrame>();
        for (var i = 0; i < 100; i++)
        {
            frames.Add(new BlackFrame(90, i * 0.5, i));
        }

        var (minimum, sceneChange) = CreditsBlackFrameAnalyzer.NormalizeThreshold(frames, 85);

        // floor = min(90, 30) = 30
        // minimum = (85 * (100 - 30) / 100) + 30 = (85 * 70 / 100) + 30 = 59 + 30 = 89
        // sceneChange = (95 * 70 / 100) + 30 = 66 + 30 = 96
        Assert.Equal(89, minimum);
        Assert.Equal(96, sceneChange);
    }

    [Fact]
    public void TestNormalizeThreshold_LowFloor()
    {
        // Frames with low 1st-percentile (5%) — floor stays at 5%, not capped
        var frames = new List<BlackFrame>();
        for (var i = 0; i < 100; i++)
        {
            frames.Add(new BlackFrame(i < 2 ? 5 : 80, i * 0.5, i));
        }

        var (minimum, sceneChange) = CreditsBlackFrameAnalyzer.NormalizeThreshold(frames, 85);

        // floor = min(5, 30) = 5
        // minimum = (85 * (100 - 5) / 100) + 5 = (85 * 95 / 100) + 5 = 80 + 5 = 85
        // sceneChange = (95 * 95 / 100) + 5 = 90 + 5 = 95
        Assert.Equal(85, minimum);
        Assert.Equal(95, sceneChange);
    }

    [Fact]
    public void TestDensityGating_RejectsLowDensityScene()
    {
        // Simulate a dark episode scene: 100 keyframes, only 20 are "black" (20% density)
        var frames = new List<BlackFrame>();
        for (var i = 0; i < 100; i++)
        {
            // Every 5th frame is 90% black, rest are 30% (below minimum of 85)
            var percentage = (i % 5 == 0) ? 90 : 30;
            frames.Add(new BlackFrame(percentage, i * 0.5, i));
        }

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 15);

        // With density gating at 50%, the scene should be rejected (only 20% density)
        Assert.Empty(scenes);
    }

    [Fact]
    public void TestDensityGating_AcceptsHighDensityScene()
    {
        // Simulate real credits: 100 keyframes, 80 are "black" (80% density)
        var frames = new List<BlackFrame>();
        for (var i = 0; i < 100; i++)
        {
            // 80% of frames are 90% black, 20% are brief non-black interruptions
            var percentage = (i % 5 == 0) ? 30 : 90;
            frames.Add(new BlackFrame(percentage, i * 0.5, i));
        }

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 15);

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
        var frames = new List<BlackFrame>();
        AddCluster(startTime: 0, startFrame: 0);
        AddCluster(startTime: 60, startFrame: 120);
        AddCluster(startTime: 120, startFrame: 240);

        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 15);

        Assert.Empty(scenes);

        void AddCluster(double startTime, int startFrame)
        {
            for (var i = 0; i < 60; i++)
            {
                var percentage = (i % 3 == 0) ? 90 : 30;
                frames.Add(new BlackFrame(percentage, startTime + (i * 0.5), startFrame + i));
            }
        }
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
        var frames = new List<BlackFrame>();

        // First credit cluster: frames 0-15 at times 0-7.5s, all 95% black (100% density)
        for (var i = 0; i < 16; i++)
        {
            frames.Add(new BlackFrame(95, i * 0.5, i));
        }

        // Gap: frames 16-53 at times 8.0-26.5s, all 10% black (non-black)
        for (var i = 16; i < 54; i++)
        {
            frames.Add(new BlackFrame(10, i * 0.5, i));
        }

        // Second credit cluster: frames 54-69 at times 27.0-34.5s, all 95% black (100% density)
        for (var i = 54; i < 70; i++)
        {
            frames.Add(new BlackFrame(95, i * 0.5, i));
        }

        // Gap between scenes: 27.0 - 7.5 = 19.5s (within MaximumTimeSkip of 20s)
        // Combined span after merge: 0-34.5s = 35s total, 32 black frames out of 70 total → ~46% density
        var scenes = CreditSceneBuilder.DetectCreditScenes(frames, 85, 96, minimumDuration: 5);

        Assert.Equal(2, scenes.Count);
        Assert.Equal(0.0, scenes[0].StartTime);
        Assert.Equal(7.5, scenes[0].EndTime);
        Assert.Equal(27.0, scenes[1].StartTime);
        Assert.Equal(34.5, scenes[1].EndTime);
    }

    [Fact]
    public void TestSelectProbeMinimum_UsesLowerOfSceneStartAndSceneChange()
    {
        var frames = new List<BlackFrame>
        {
            new(20, 0.0, 0),
            new(30, 0.5, 1),
            new(92, 1.0, 2),
            new(95, 1.5, 3),
        };

        var scene = new CreditScene(2, 3, 1.0, 1.5);

        var probeMinimum = CreditsBoundaryHelper.SelectProbeMinimum(frames, scene, sceneChange: 95);

        Assert.Equal(92, probeMinimum);
    }

    [Fact]
    public void TestSelectProbeMinimum_CapsAtSceneChange()
    {
        var frames = new List<BlackFrame>
        {
            new(20, 0.0, 0),
            new(30, 0.5, 1),
            new(99, 1.0, 2),
            new(99, 1.5, 3),
        };

        var scene = new CreditScene(2, 3, 1.0, 1.5);

        var probeMinimum = CreditsBoundaryHelper.SelectProbeMinimum(frames, scene, sceneChange: 95);

        Assert.Equal(95, probeMinimum);
    }

    [Fact]
    public void TestSelectProbeMinimum_MissingStartFrame_FallsBackToSceneChange()
    {
        // A scene whose StartFrame is not present in the keyframe list (e.g. interval-derived)
        // must fall back to sceneChange rather than throwing from First().
        var frames = new List<BlackFrame>
        {
            new(50, 0.0, 0),
            new(60, 0.5, 1),
        };

        var scene = new CreditScene(99, 100, 10.0, 12.0);

        var probeMinimum = CreditsBoundaryHelper.SelectProbeMinimum(frames, scene, sceneChange: 95);

        Assert.Equal(95, probeMinimum);
    }

    [Fact]
    public void TestShouldRefineBoundary_SkipsSmallKeyframeGap()
    {
        var scene = new CreditScene(20, 40, 10.4, 30.0);

        var shouldRefine = CreditsBoundaryHelper.ShouldRefineBoundary(scene, lastKeyframeTime: 10.0, minimumDuration: 15);

        Assert.False(shouldRefine);
    }

    [Fact]
    public void TestShouldRefineBoundary_SkipsSceneThatCannotReachMinimumDuration()
    {
        var scene = new CreditScene(20, 40, 10.0, 20.0);

        var shouldRefine = CreditsBoundaryHelper.ShouldRefineBoundary(scene, lastKeyframeTime: 8.0, minimumDuration: 15);

        Assert.False(shouldRefine);
    }

    [Fact]
    public void TestShouldRefineBoundary_AcceptsMeaningfulBoundaryWindow()
    {
        var scene = new CreditScene(20, 40, 10.0, 24.0);

        var shouldRefine = CreditsBoundaryHelper.ShouldRefineBoundary(scene, lastKeyframeTime: 8.5, minimumDuration: 15);

        Assert.True(shouldRefine);
    }

    [Fact]
    public void TestTryRefineBoundaryTime_RejectsProbeAtPrecedingKeyframe()
    {
        var refined = CreditsBoundaryHelper.TryRefineBoundaryTime(
            probeTime: 0.0,
            lastKeyframeTime: 10.0,
            sceneStartTime: 15.0);

        Assert.Null(refined);
    }

    [Fact]
    public void TestTryRefineBoundaryTime_AcceptsProbeInsideBoundaryWindow()
    {
        var refined = CreditsBoundaryHelper.TryRefineBoundaryTime(
            probeTime: 2.5,
            lastKeyframeTime: 10.0,
            sceneStartTime: 15.0);

        Assert.Equal(12.5, refined);
    }

    [Fact]
    public void TestTryRefineBoundaryTime_AcceptsProbeAtExactSceneStart()
    {
        // When probeTime + lastKeyframeTime == sceneStartTime, the refinement
        // lands exactly at the original scene start (a no-op). This should be
        // accepted, not rejected — guarding against an accidental > to >= change.
        var refined = CreditsBoundaryHelper.TryRefineBoundaryTime(
            probeTime: 5.0,
            lastKeyframeTime: 10.0,
            sceneStartTime: 15.0);

        Assert.Equal(15.0, refined);
    }

    [Fact]
    public void TestTryRefineBoundaryTime_RejectsProbeAfterSceneStart()
    {
        var refined = CreditsBoundaryHelper.TryRefineBoundaryTime(
            probeTime: 6.0,
            lastKeyframeTime: 10.0,
            sceneStartTime: 15.0);

        Assert.Null(refined);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_EmptyScan_ReturnsNull()
    {
        var ffmpeg = new FakeFFmpegService([]);
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
        var ffmpeg = new FakeFFmpegService(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
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
        var ffmpeg = new FakeFFmpegService(CreateDenseFrames(startTime: 0, endTime: 10, percentage: 95));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_DarkLowDensityScene_ReturnsNull()
    {
        var frames = new List<BlackFrame>();
        for (var i = 0; i < 100; i++)
        {
            frames.Add(new BlackFrame(i % 5 == 0 ? 95 : 30, i * 0.5, i));
        }

        var ffmpeg = new FakeFFmpegService([.. frames]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_LowDensitySingleCandidateUsesIntervalSupport()
    {
        var ffmpeg = new FakeFFmpegService(
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
        var ffmpeg = new FakeFFmpegService(CreateLowDensitySingleCandidateFrames());
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
        Assert.Equal(1, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_StingerSplit_ReturnsFinalScene()
    {
        var ffmpeg = new FakeFFmpegService(CreateStingerSplitFrames());
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
        var ffmpeg = new FakeFFmpegService(
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
    public async Task TestDetectCreditsAsync_ValidBlackFrameSceneDoesNotRequireBlackIntervals()
    {
        var ffmpeg = new FakeFFmpegService(CreateStingerSplitFrames())
        {
            IntervalScanException = new NotSupportedException("blackdetect unavailable"),
        };
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
        var frames = new List<BlackFrame>
        {
            new(15, 366.45, 36),
            new(96, 376.46, 37),
            new(96, 386.47, 38),
            new(98, 396.48, 39),
            new(99, 406.49, 40),
            new(20, 416.5, 41),
        };
        var ffmpeg = new FakeFFmpegService(
            [.. frames],
            intervals: [new BlackInterval(367.827, 376.002)]);
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
        var ffmpeg = new FakeFFmpegService(frames);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        SetRefineCreditsBoundary(analyzer, value: false);
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
        var ffmpeg = new FakeFFmpegService(
            frames,
            intervals: [new BlackInterval(5, 19.8)]);
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
        var frames = new List<BlackFrame>
        {
            new(15, 366.45, 36),
            new(20, 376.46, 37),
            new(18, 386.47, 38),
            new(22, 396.48, 39),
        };
        var ffmpeg = new FakeFFmpegService(
            [.. frames],
            intervals: [new BlackInterval(367.827, 376.002)]);
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
    public void TestRankCreditCandidates_SelectsExpectedScene(
        string label,
        CreditScene[] scenes,
        BlackInterval[] intervals,
        int expectedIndex)
    {
        Assert.False(string.IsNullOrWhiteSpace(label));

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
        yield return ["no intervals -> latest scene", twoScenes, Array.Empty<BlackInterval>(), 1];

        // An interval overlapping the earlier scene beyond the minimum promotes it over the later scene.
        yield return ["interval promotes earlier scene", twoScenes, new[] { new BlackInterval(205, 246) }, 0];

        // Overlap shorter than MinimumIntervalOverlapSeconds (0.25s) is not support: the latest scene wins.
        yield return ["sub-threshold overlap is not support", twoScenes, new[] { new BlackInterval(259.9, 280) }, 1];

        // An interval supporting the later scene keeps the latest scene selected.
        yield return ["interval supports later scene", twoScenes, new[] { new BlackInterval(315, 360) }, 1];

        CreditScene[] threeScenes =
        [
            new(100, 200, 50, 90),
            new(300, 400, 150, 190),
            new(500, 600, 250, 290),
        ];

        // Among supported scenes the latest supported one wins, ahead of an unsupported later scene.
        yield return
        [
            "latest supported scene beats unsupported later scene",
            threeScenes,
            new[] { new BlackInterval(55, 95), new BlackInterval(155, 195) },
            1,
        ];
    }

    [Fact]
    public async Task TestDetectCreditsAsync_Cancellation_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ffmpeg = new FakeFFmpegService(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => analyzer.DetectCreditsAsync(episode, 85, 32, 15, cts.Token));
    }

    [Fact]
    public async Task TestDetectCreditsAsync_RefinesBoundaryByDefault()
    {
        var frames = new List<BlackFrame>();
        frames.AddRange(CreateDenseFrames(startTime: 0, endTime: 8, percentage: 30));
        frames.AddRange(CreateDenseFrames(startTime: 10, endTime: 30, percentage: 95, startFrame: 20));

        var ffmpeg = new FakeFFmpegService([.. frames], [new BlackFrame(95, 1.25, 0)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(109.25, result.Start);
        Assert.Equal(130, result.End);
        Assert.Equal(1, ffmpeg.RangeScanCalls);
        var probeRange = Assert.IsType<TimeRange>(ffmpeg.LastProbeRange);
        Assert.Equal(108, probeRange.Start);
        Assert.Equal(110, probeRange.End);
        Assert.Equal(95, ffmpeg.LastProbeMinimum);
        Assert.Equal(32, ffmpeg.LastProbeThreshold);
        Assert.Equal(AnalysisMode.Credits, ffmpeg.LastProbeMode);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_RefinesSubMinimumFinalSceneBeforeSelectingEarlierScene()
    {
        var frames = new List<BlackFrame>();
        frames.AddRange(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
        frames.AddRange(CreateDenseFrames(startTime: 20.5, endTime: 58, percentage: 30));
        frames.AddRange(CreateDenseFrames(startTime: 60, endTime: 74, percentage: 95, startFrame: 120));

        var ffmpeg = new FakeFFmpegService([.. frames], [new BlackFrame(95, 0.5, 0)]);
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
        var frames = new List<BlackFrame>();
        frames.AddRange(CreateDenseFrames(startTime: 0, endTime: 8, percentage: 30));
        frames.AddRange(CreateDenseFrames(startTime: 10, endTime: 30, percentage: 95, startFrame: 20));

        var ffmpeg = new FakeFFmpegService([.. frames], [new BlackFrame(95, 1.25, 0)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        SetRefineCreditsBoundary(analyzer, value: false);
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
        var frames = new List<BlackFrame>();
        frames.AddRange(CreateDenseFrames(startTime: 0, endTime: 8, percentage: 30));
        frames.AddRange(CreateDenseFrames(startTime: 14, endTime: 24, percentage: 95, startFrame: 40));

        // The only keyframe scene is too short on its own and could reach the minimum duration only via
        // boundary refinement. With refinement disabled it must not be admitted, so the interval fallback
        // can still recover the credits instead of the analyzer returning null.
        var ffmpeg = new FakeFFmpegService([.. frames], intervals: [new BlackInterval(8, 24)]);
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        SetRefineCreditsBoundary(analyzer, value: false);
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
        var analyzer = CreateCreditsBlackFrameAnalyzer(new FakeFFmpegService([]));

        await Assert.ThrowsAsync<NotImplementedException>(
            () => analyzer.AnalyzeMediaFiles([], AnalysisMode.Introduction, CancellationToken.None));
    }

    [Fact]
    public async Task TestAnalyzeMediaFiles_SkipsAlreadyAnalyzedEpisodes()
    {
        var episode = CreateQueuedCreditsEpisode();
        episode.SetAnalyzed(AnalysisMode.Credits, EpisodeState.Analyzed);
        var ffmpeg = new FakeFFmpegService(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
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
        var ffmpeg = new FakeFFmpegService(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
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
        var ffmpeg = new FakeFFmpegService([])
        {
            CreditsScanException = new InvalidOperationException("test failure"),
        };
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());

        var result = await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Credits, CancellationToken.None);

        Assert.Same(episode, result[0]);
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Credits));
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

    [Fact]
    public void TestCreditEntropyFallback_LaterSparseCreditsNotConstrainedByEarlierDenseRun()
    {
        // Regression (Finding 1): an earlier dense card-like run (0-20s) must not capture or extend into
        // a later long-GOP credit run (60-96s, 12s cadence). Real content separates the two groups, so
        // the run splits there and the latest sustained run is returned, not the earlier one.
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= 20; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30));
        }

        for (var time = 22.0; time < 60; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, 0.55, 108)); // real content between the two card groups
        }

        foreach (var time in new[] { 60.0, 72.0, 84.0, 96.0 })
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30));
        }

        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15);

        Assert.NotNull(range);
        Assert.Equal(60, range!.Start);
        Assert.Equal(96, range.End);
    }

    [Fact]
    public void TestCreditEntropyFallback_KeepsAllCardRunWhenGopExceedsBridge()
    {
        // Regression: a real all-card credit run whose keyframe cadence is just above the fixed
        // MaximumSceneMergeGapSeconds bridge (cards every 21s, no non-card frames between). With no
        // intervening non-card evidence the run must stay whole, not split into one-frame runs that
        // each fail the minimum duration and yield null.
        var visuals = new List<KeyframeVisual>();
        foreach (var time in new[] { 0.0, 21.0, 42.0, 63.0 })
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30));
        }

        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 60);

        Assert.NotNull(range);
        Assert.Equal(0, range!.Start);
        Assert.Equal(63, range.End);
    }

    [Fact]
    public void TestCreditEntropyFallback_GroupsSparseCardsAfterDenseContent()
    {
        // Regression (Finding 1): dense 2s non-card content then static credit cards on a long-GOP
        // (12s keyframe) source. Grouping must key off the card cadence, not the dense content cadence,
        // or every 12s card gap splits the run and the 36s credit sequence is missed entirely.
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= 58; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, 0.55, 108));
        }

        foreach (var time in new[] { 60.0, 72.0, 84.0, 96.0 })
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30));
        }

        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15);

        Assert.NotNull(range);
        Assert.Equal(60, range!.Start);
        Assert.Equal(96, range.End);
    }

    [Fact]
    public void TestCreditEntropyFallback_TrimsSparseTailPastSubstantialDenseBody()
    {
        // Regression (Finding 2): a substantial dense credit block followed by isolated near-uniform
        // frames every 8s out to the window edge. The trim must anchor to the dense-body cadence so the
        // sparse tail is cut back to the real credit block instead of over-extending to the last stray.
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= 20; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30));
        }

        for (var time = 28.0; time <= 196; time += 8)
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30));
        }

        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15);

        Assert.NotNull(range);
        Assert.Equal(0, range!.Start);
        Assert.Equal(20, range.End);
    }

    [Fact]
    public void TestCreditEntropyFallback_RejectsIsolatedCardsBridgingBusyContent()
    {
        // Regression: two card-like keyframes 18s apart with busy/high-entropy keyframes every 2s
        // between them must NOT form credits — that is normal content with occasional static shots,
        // not a sustained low-entropy card sequence.
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= 18; time += 2)
        {
            var card = time is 0 or 18;
            visuals.Add(new KeyframeVisual(time, card ? 0.12 : 0.55, card ? 30 : 108));
        }

        Assert.Null(CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15));
    }

    [Fact]
    public void TestCreditEntropyFallback_DetectsLowEntropyCardRun()
    {
        var visuals = CreateCardCreditVisuals(cardStart: 30, cardEnd: 54);

        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15);

        Assert.NotNull(range);
        Assert.Equal(30, range.Start);
        Assert.Equal(54, range.End);
    }

    [Fact]
    public void TestCreditEntropyFallback_RejectsHighEntropyDarkScene()
    {
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time < 60; time += 2)
        {
            // Dark (low luma) but detailed/non-uniform: high entropy, like a night scene.
            visuals.Add(new KeyframeVisual(time, 0.63, 50));
        }

        Assert.Null(CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15));

        var saturatedCards = CreateCardCreditVisuals(cardStart: 0, cardEnd: 20, cardSaturation: 200);
        Assert.Null(CreditEntropyFallback.FindCreditRange(saturatedCards, minimumDuration: 15));
    }

    [Fact]
    public void TestCreditEntropyFallback_RejectsSubMinimumDurationRun()
    {
        var visuals = CreateCardCreditVisuals(cardStart: 30, cardEnd: 40); // only 10s of card

        Assert.Null(CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15));
    }

    [Fact]
    public void TestCreditEntropyFallback_SelectsLatestQualifyingRun()
    {
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= 20; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30)); // early card block 0-20
        }

        for (var time = 22.0; time < 60; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, 0.55, 108)); // long busy gap
        }

        for (var time = 60.0; time <= 80; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30)); // late card block 60-80
        }

        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15);

        Assert.NotNull(range);
        Assert.Equal(60, range.Start);
        Assert.Equal(80, range.End);
    }

    [Fact]
    public void TestCreditEntropyFallback_TrimsOverExtendedTail()
    {
        // Regression: real credits 0-20s, then non-credit tail with an isolated near-uniform frame
        // every 8s. Before the trailing-density trim these periodic cards bridged the run out to 54s
        // (an over-skip into post-credits content); the end must anchor to the dense block at 20s.
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= 20; time += 2)
        {
            visuals.Add(new KeyframeVisual(time, 0.12, 30));
        }

        for (var time = 22.0; time <= 58; time += 2)
        {
            var isolatedCard = time is 30 or 38 or 46 or 54;
            visuals.Add(isolatedCard ? new KeyframeVisual(time, 0.12, 30) : new KeyframeVisual(time, 0.55, 108));
        }

        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15);

        Assert.NotNull(range);
        Assert.Equal(0, range!.Start);
        Assert.Equal(20, range.End);
    }

    [Fact]
    public void TestCreditEntropyFallback_TrimsIsolatedLeadingCard()
    {
        // Regression: an isolated low-entropy card at 36s bridges (on a 4s-GOP source) into the real
        // dense credits at 52-80s. The start must anchor to the dense block at 52s, not the pre-card.
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time <= 88; time += 4)
        {
            var card = time == 36 || (time >= 52 && time <= 80);
            visuals.Add(new KeyframeVisual(time, card ? 0.12 : 0.55, card ? 30 : 108));
        }

        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration: 15);

        Assert.NotNull(range);
        Assert.Equal(52, range!.Start);
        Assert.Equal(80, range.End);
    }

    [Fact]
    public void TestCreditEntropyFallback_TrailingTrimBracket()
    {
        static List<KeyframeVisual> Seq(double end, double step, params (double From, double To)[] cards)
        {
            var v = new List<KeyframeVisual>();
            for (var t = 0.0; t <= end + 1e-9; t += step)
            {
                var card = false;
                foreach (var ci in cards)
                {
                    if (t >= ci.From - 1e-9 && t <= ci.To + 1e-9)
                    {
                        card = true;
                        break;
                    }
                }

                v.Add(new KeyframeVisual(t, card ? 0.12 : 0.55, card ? 30 : 108));
            }

            return v;
        }

        static (double Start, double End)? Run(List<KeyframeVisual> v)
        {
            var r = CreditEntropyFallback.FindCreditRange(v, 15);
            return r is null ? null : (r.Start, r.End);
        }

        // Over-extension: dense credits 0-20, periodic isolated tail cards every 8s -> trim to 20.
        Assert.Equal((0.0, 20.0), Run(Seq(58, 2, (0, 20), (30, 30), (38, 38), (46, 46), (54, 54))));

        // Leading over-extension: an isolated pre-credit card bridges into a dense block (4s GOP)
        // -> start anchored to the dense block, not the stray pre-card.
        Assert.Equal((52.0, 80.0), Run(Seq(88, 4, (36, 36), (52, 80))));

        // Clean dense card run -> unchanged.
        Assert.Equal((30.0, 54.0), Run(Seq(54, 2, (30, 54))));

        // Mid-body ident interlude (6s of non-card bracketed by dense cards) -> preserved.
        Assert.Equal((0.0, 60.0), Run(Seq(60, 2, (0, 28), (36, 60))));

        // Interlude near the end (cards resume densely after) -> preserved.
        Assert.Equal((0.0, 60.0), Run(Seq(60, 2, (0, 48), (56, 60))));

        // Sparse all-card credits (8s GOP) -> kept (100% density, trailing gap within scaled trim).
        Assert.Equal((0.0, 40.0), Run(Seq(40, 8, (0, 40))));

        // Uniform sparse long-GOP credits (12s cadence) -> kept; the trim keys off the run's own
        // cadence, so an all-card run is never discarded even when its gap exceeds the capped bridge.
        Assert.Equal((0.0, 48.0), Run(Seq(48, 12, (0, 48))));

        // Two real runs separated by a long gap -> latest selected.
        Assert.Equal((60.0, 80.0), Run(Seq(80, 2, (0, 20), (60, 80))));

        // Sparse isolated cards bridged across busy 2s content (brief dense head, then a lone card
        // every 8s) -> rejected by the card-density floor: most keyframes in the span are busy
        // content, so this reads as normal content with occasional static shots, not a card sequence.
        Assert.Null(Run(Seq(54, 2, (0, 6), (14, 14), (22, 22), (30, 30), (38, 38), (46, 46), (54, 54))));

        // Final card spaced just within cadence (4s) -> kept, not over-trimmed.
        Assert.Equal((0.0, 44.0), Run(Seq(44, 2, (0, 40), (44, 44))));

        // All busy content -> null.
        Assert.Null(Run(Seq(60, 2)));
    }

    [Fact]
    public async Task TestDetectCreditsAsync_NonBlackCreditsFallback_DetectsCardCredits()
    {
        var ffmpeg = new FakeFFmpegService(
            CreateDenseFrames(startTime: 0, endTime: 54, percentage: 0), // no black frames anywhere
            keyframeVisuals: CreateCardCreditVisuals(cardStart: 30, cardEnd: 54));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(130, result.Start);
        Assert.Equal(154, result.End);
        Assert.Equal(1, ffmpeg.VisualScanCalls);
        Assert.Equal(0, ffmpeg.IntervalScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_NoBlackFramesAtAll_RunsFallback()
    {
        // Pins the literal blackFrames.Count == 0 branch (the black scan emits nothing), distinct from
        // the "frames present but no valid black-credit scene" path the other fallback test exercises.
        var ffmpeg = new FakeFFmpegService(
            [],
            keyframeVisuals: CreateCardCreditVisuals(cardStart: 30, cardEnd: 54));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

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
        var ffmpeg = new FakeFFmpegService(
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
        var ffmpeg = new FakeFFmpegService(
            CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95),
            keyframeVisuals: CreateCardCreditVisuals(cardStart: 0, cardEnd: 20));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.NotNull(result);
        Assert.Equal(100, result.Start);
        Assert.Equal(0, ffmpeg.VisualScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_NonBlackCreditsDisabled_SkipsFallback()
    {
        var ffmpeg = new FakeFFmpegService(
            CreateDenseFrames(startTime: 0, endTime: 54, percentage: 0),
            keyframeVisuals: CreateCardCreditVisuals(cardStart: 30, cardEnd: 54));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        SetDetectNonBlackCredits(analyzer, value: false);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
        Assert.Equal(0, ffmpeg.VisualScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_NoBlackFramesAtAll_DisabledSkipsFallback()
    {
        // Disabled-setting companion for the empty-scan branch (blackFrames.Count == 0): with the
        // option off, an empty black scan must still honor _config.DetectNonBlackCredits and never
        // start the keyframe-visual fallback. The other disabled test seeds non-empty pblack=0 frames,
        // so only the Count > 0 path is otherwise covered; this pins the empty-scan branch split.
        var ffmpeg = new FakeFFmpegService(
            [],
            keyframeVisuals: CreateCardCreditVisuals(cardStart: 30, cardEnd: 54));
        var analyzer = CreateCreditsBlackFrameAnalyzer(ffmpeg);
        SetDetectNonBlackCredits(analyzer, value: false);
        var episode = CreateQueuedCreditsEpisode();

        var result = await analyzer.DetectCreditsAsync(episode, 85, 32, 15);

        Assert.Null(result);
        Assert.Equal(0, ffmpeg.VisualScanCalls);
    }

    // ── Fingerprint-based integration tests ──────────────────────────────

    [Fact]
    public void TestFingerprint_Alt3_CleanCredits_SingleScene()
    {
        // alt-3: fewest frames (211), clean credits, no transition-frame shift.
        var frames = ParseFingerprintFile("blackframe-alt-3");

        // Verify normalization: floor=0 → thresholds are pass-through values
        var (minimum, sceneChange) = CreditsBlackFrameAnalyzer.NormalizeThreshold(frames, 85);
        Assert.Equal(85, minimum);
        Assert.Equal(95, sceneChange);

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

        var (minimum, sceneChange) = CreditsBlackFrameAnalyzer.NormalizeThreshold(frames, 85);
        Assert.Equal(85, minimum);
        Assert.Equal(95, sceneChange);

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
        // alt-5: mid-credit stinger creates two separate credit blocks.
        // Non-zero 1st-percentile floor (25) exercises NormalizeThreshold scaling.
        var frames = ParseFingerprintFile("blackframe-alt-5");

        // Verify normalization: floor=25 scales thresholds upward
        var (minimum, sceneChange) = CreditsBlackFrameAnalyzer.NormalizeThreshold(frames, 85);
        Assert.Equal(88, minimum);   // (85 * 75 / 100) + 25 = 88
        Assert.Equal(96, sceneChange); // (95 * 75 / 100) + 25 = 96

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

    [Fact]
    public void TestParseFingerprintFile_RootedPathThrows()
    {
        var rootedPath = Path.GetFullPath("blackframe-alt-3");

        Assert.Throws<ArgumentException>(() => ParseFingerprintFile(rootedPath));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a raw FFmpeg blackframe filter output file into a list of <see cref="BlackFrame"/> records.
    /// Delegates to the production <see cref="FFmpegOutputParser.ParseBlackFrame"/> parser to avoid
    /// regex/format drift between tests and implementation.
    /// </summary>
    private static List<BlackFrame> ParseFingerprintFile(string filename)
    {
        if (Path.IsPathRooted(filename))
        {
            throw new ArgumentException("Fingerprint filename must be a relative path.", nameof(filename));
        }

        var path = Path.Combine("..", "..", "..", "fingerprints", filename);
        var raw = File.ReadAllText(path);
        return [.. FFmpegOutputParser.ParseBlackFrames(raw)];
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

    private static CreditsBlackFrameAnalyzer CreateCreditsBlackFrameAnalyzer(IFFmpegService ffmpegService)
    {
        return new(NullLogger<CreditsBlackFrameAnalyzer>.Instance, ffmpegService, DatabaseTestHelpers.CreateTempSegmentDatabase());
    }

    private static void SetRefineCreditsBoundary(CreditsBlackFrameAnalyzer analyzer, bool value)
    {
        var config = (PluginConfiguration)EntrypointTestHelpers.GetPrivateField(analyzer, "_config");
        config.RefineCreditsBoundary = value;
    }

    private static void SetDetectNonBlackCredits(CreditsBlackFrameAnalyzer analyzer, bool value)
    {
        var config = (PluginConfiguration)EntrypointTestHelpers.GetPrivateField(analyzer, "_config");
        config.DetectNonBlackCredits = value;
    }

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

    private static BlackFrame[] CreateStingerSplitFrames()
    {
        var frames = new List<BlackFrame>();
        frames.AddRange(CreateDenseFrames(startTime: 0, endTime: 20, percentage: 95));
        frames.AddRange(CreateDenseFrames(startTime: 20.5, endTime: 89.5, percentage: 30, startFrame: 41));
        frames.AddRange(CreateDenseFrames(startTime: 90, endTime: 120, percentage: 95, startFrame: 180));

        return [.. frames];
    }

    private static BlackFrame[] CreateLowDensitySingleCandidateFrames()
    {
        var frames = new List<BlackFrame>();
        for (var i = 0; i < 100; i++)
        {
            var percentage = (i % 3 == 0) ? 90 : 30;
            frames.Add(new BlackFrame(percentage, i * 0.5, i));
        }

        return [.. frames];
    }

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

    private static QueuedEpisode QueueFile(string path)
    {
        return new()
        {
            EpisodeId = Guid.NewGuid(),
            Name = path,
            Path = "../../../video/" + path
        };
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

    private static FFmpegService CreateFFmpegService()
    {
        return new FFmpegService(
            NullLogger<FFmpegService>.Instance,
            DatabaseTestHelpers.CreateTempCacheService());
    }

    private static BlackFrameAnalyzer CreateBlackFrameAnalyzer()
    {
        return new(NullLogger<BlackFrameAnalyzer>.Instance, CreateFFmpegService(), DatabaseTestHelpers.CreateTempSegmentDatabase());
    }

    private static ChapterInfo CreateChapterInfo(double startSeconds)
    {
        return new()
        {
            StartPositionTicks = TimeSpan.FromSeconds(startSeconds).Ticks
        };
    }

    private class ChapterManagerProxy : DispatchProxy
    {
        public IReadOnlyList<ChapterInfo> Chapters { get; set; } = [];

        public static IChapterManager Create(IReadOnlyList<ChapterInfo> chapters)
        {
            var proxy = Create<IChapterManager, ChapterManagerProxy>();
            ((ChapterManagerProxy)(object)proxy).Chapters = chapters;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IChapterManager.GetChapters))
            {
                return Chapters;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    private sealed class RangeBasedBlackFrameService(IReadOnlyList<TimeRange> blackRanges) : IFFmpegService
    {
        public Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, TimeRange range, int minimum, int threshold, AnalysisMode mode, CancellationToken cancellationToken = default)
        {
            var hasBlackFrames = blackRanges.Any(r => range.Start >= r.Start && range.Start < r.End);
            return Task.FromResult(hasBlackFrames ? [new BlackFrame(95, 0, 0)] : Array.Empty<BlackFrame>());
        }

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string GetChromaprintLogs() => string.Empty;
    }

    private sealed class FakeFFmpegService(
        BlackFrame[] creditsFrames,
        BlackFrame[]? probeFrames = null,
        BlackInterval[]? intervals = null,
        KeyframeVisual[]? keyframeVisuals = null) : IFFmpegService
    {
        private readonly BlackFrame[] _creditsFrames = creditsFrames;
        private readonly BlackFrame[] _probeFrames = probeFrames ?? [];
        private readonly BlackInterval[] _intervals = intervals ?? [];
        private readonly KeyframeVisual[] _keyframeVisuals = keyframeVisuals ?? [];

        public Exception? CreditsScanException { get; init; }

        public Exception? IntervalScanException { get; init; }

        public int CreditsScanCalls { get; private set; }

        public int IntervalScanCalls { get; private set; }

        public int RangeScanCalls { get; private set; }

        public int VisualScanCalls { get; private set; }

        public TimeRange? LastProbeRange { get; private set; }

        public int? LastProbeMinimum { get; private set; }

        public int? LastProbeThreshold { get; private set; }

        public AnalysisMode? LastProbeMode { get; private set; }

        public int? LastIntervalThreshold { get; private set; }

        public TimeRange? LastIntervalRange { get; private set; }

        public Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BlackFrame[]> DetectBlackFramesAsync(
            QueuedEpisode episode,
            TimeRange range,
            int minimum,
            int threshold,
            AnalysisMode mode,
            CancellationToken cancellationToken = default)
        {
            RangeScanCalls++;
            LastProbeRange = range;
            LastProbeMinimum = minimum;
            LastProbeThreshold = threshold;
            LastProbeMode = mode;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_probeFrames);
        }

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
        {
            CreditsScanCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (CreditsScanException is not null)
            {
                throw CreditsScanException;
            }

            return Task.FromResult(_creditsFrames);
        }

        public Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
        {
            VisualScanCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_keyframeVisuals);
        }

        public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default)
        {
            IntervalScanCalls++;
            LastIntervalRange = range;
            LastIntervalThreshold = threshold;
            cancellationToken.ThrowIfCancellationRequested();
            if (IntervalScanException is not null)
            {
                throw IntervalScanException;
            }

            return Task.FromResult(_intervals);
        }

        public Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string GetChromaprintLogs() => string.Empty;
    }
}
