// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;
using Xunit;

public class TestBlackFrames
{
    [FactSkipFFmpegTests]
    public void TestBlackFrameDetection()
    {
        var range = 1e-5;

        var expected = new List<BlackFrame>();
        expected.AddRange(CreateFrameSequence(2, 3));
        expected.AddRange(CreateFrameSequence(5, 6));
        expected.AddRange(CreateFrameSequence(8, 9.96));

        var actual = FFmpegWrapper.DetectBlackFrames(QueueFile("rainbow.mp4"), new(0, 10), 85, 32);

        for (var i = 0; i < expected.Count; i++)
        {
            var (e, a) = (expected[i], actual[i]);
            Assert.Equal(e.Percentage, a.Percentage);
            Assert.InRange(a.Time, e.Time - range, e.Time + range);
        }
    }

    [FactSkipFFmpegTests]
    public void TestEndCreditDetection()
    {
        // new strategy new range
        var range = 3;

        var analyzer = CreateBlackFrameAnalyzer();

        var episode = QueueFile("credits.mp4");
        episode.Duration = (int)new TimeSpan(0, 5, 30).TotalSeconds;

        var result = analyzer.AnalyzeMediaFile(episode, 240, 85, 32);
        Assert.NotNull(result);
        Assert.InRange(result.Start, 300 - range, 300 + range);
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
        var scenes = BlackFrameAltAnalyzer.DetectCreditScenes(frames, 85, 96);

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

        var scenes = BlackFrameAltAnalyzer.DetectCreditScenes(frames, 85, 96);

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

        var (minimum, sceneChange) = BlackFrameAltAnalyzer.NormalizeThreshold(frames, 85);

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

        var (minimum, sceneChange) = BlackFrameAltAnalyzer.NormalizeThreshold(frames, 85);

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

        var scenes = BlackFrameAltAnalyzer.DetectCreditScenes(frames, 85, 96);

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

        var scenes = BlackFrameAltAnalyzer.DetectCreditScenes(frames, 85, 96);

        // With density gating at 50%, the scene should be accepted (80% density)
        Assert.NotEmpty(scenes);
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
        var result = BlackFrameAltAnalyzer.FindBoundaryKeyframeTimes(frames, scene);
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

        var result = BlackFrameAltAnalyzer.FindBoundaryKeyframeTimes(frames, scene);
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

        var result = BlackFrameAltAnalyzer.FindBoundaryKeyframeTimes(frames, scene);
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
        var scenes = BlackFrameAltAnalyzer.DetectCreditScenes(frames, 85, 96);

        Assert.Equal(2, scenes.Count);
        Assert.Equal(0.0, scenes[0].StartTime);
        Assert.Equal(7.5, scenes[0].EndTime);
        Assert.Equal(27.0, scenes[1].StartTime);
        Assert.Equal(34.5, scenes[1].EndTime);
    }

    [Fact]
    public void TestConvertProbeTimestamp_ConvertsToRelativeTime()
    {
        // Simulate: CreditsFingerprintStart=240s, lastKeyframeTime=55s (relative to CreditsFingerprintStart),
        // probeStart = 55 + 240 = 295s (absolute seek point passed to FFmpeg).
        // FFmpeg returns probeTime=2.5s (relative to seek point 295s).
        // Expected: absoluteTime = 2.5 + 295 = 297.5s
        //           relativeTime = 297.5 - 240 = 57.5s = probeTime + lastKeyframeTime
        var result = BlackFrameAltAnalyzer.ConvertProbeTimestamp(probeTime: 2.5, lastKeyframeTime: 55.0);
        Assert.Equal(57.5, result);
    }

    [Fact]
    public void TestConvertProbeTimestamp_ZeroProbeTime_ReturnsLastKeyframeTime()
    {
        // When the first probe frame is at the seek point itself (probeTime=0),
        // the refined time equals the preceding keyframe time.
        var result = BlackFrameAltAnalyzer.ConvertProbeTimestamp(probeTime: 0.0, lastKeyframeTime: 30.0);
        Assert.Equal(30.0, result);
    }

    [Fact]
    public void TestConvertProbeTimestamp_ZeroLastKeyframeTime()
    {
        // Edge case: preceding keyframe is at the very start (time 0).
        var result = BlackFrameAltAnalyzer.ConvertProbeTimestamp(probeTime: 1.5, lastKeyframeTime: 0.0);
        Assert.Equal(1.5, result);
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

        var probeMinimum = BlackFrameAltAnalyzer.SelectProbeMinimum(frames, scene, sceneChange: 95);

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

        var probeMinimum = BlackFrameAltAnalyzer.SelectProbeMinimum(frames, scene, sceneChange: 95);

        Assert.Equal(95, probeMinimum);
    }

    [Fact]
    public void TestTryRefineBoundaryTime_RejectsProbeAtPrecedingKeyframe()
    {
        var refined = BlackFrameAltAnalyzer.TryRefineBoundaryTime(
            probeTime: 0.0,
            lastKeyframeTime: 10.0,
            sceneStartTime: 15.0);

        Assert.Null(refined);
    }

    [Fact]
    public void TestTryRefineBoundaryTime_AcceptsProbeInsideBoundaryWindow()
    {
        var refined = BlackFrameAltAnalyzer.TryRefineBoundaryTime(
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
        var refined = BlackFrameAltAnalyzer.TryRefineBoundaryTime(
            probeTime: 5.0,
            lastKeyframeTime: 10.0,
            sceneStartTime: 15.0);

        Assert.Equal(15.0, refined);
    }

    [Fact]
    public void TestTryRefineBoundaryTime_RejectsProbeAfterSceneStart()
    {
        var refined = BlackFrameAltAnalyzer.TryRefineBoundaryTime(
            probeTime: 6.0,
            lastKeyframeTime: 10.0,
            sceneStartTime: 15.0);

        Assert.Null(refined);
    }

    // ── Fingerprint-based integration tests ──────────────────────────────

    [Fact]
    public void TestFingerprint_Alt3_CleanCredits_SingleScene()
    {
        // alt-3: fewest frames (211), clean credits, no transition-frame shift.
        var frames = ParseFingerprintFile("blackframe-alt-3");

        // Verify normalization: floor=0 → thresholds are pass-through values
        var (minimum, sceneChange) = BlackFrameAltAnalyzer.NormalizeThreshold(frames, 85);
        Assert.Equal(85, minimum);
        Assert.Equal(95, sceneChange);

        var scenes = BlackFrameAltAnalyzer.DetectCreditScenes(frames, minimum, sceneChange);

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

        var (minimum, sceneChange) = BlackFrameAltAnalyzer.NormalizeThreshold(frames, 85);
        Assert.Equal(85, minimum);
        Assert.Equal(95, sceneChange);

        var scenes = BlackFrameAltAnalyzer.DetectCreditScenes(frames, minimum, sceneChange);

        // 4 scenes survive density gating; none merge (gaps > 20s).
        Assert.Equal(4, scenes.Count);

        // The real credits are the last scene (backward iteration would pick this first).
        // Before transition-frame search: start=422.843s
        // After: first frame >= sceneChange (95) shifts start to 463.425s
        var credits = scenes[3];
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
        var (minimum, sceneChange) = BlackFrameAltAnalyzer.NormalizeThreshold(frames, 85);
        Assert.Equal(88, minimum);   // (85 * 75 / 100) + 25 = 88
        Assert.Equal(96, sceneChange); // (95 * 75 / 100) + 25 = 96

        var scenes = BlackFrameAltAnalyzer.DetectCreditScenes(frames, minimum, sceneChange);

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
    /// Delegates to the production <see cref="FFmpegWrapper.ParseBlackFrame"/> parser to avoid
    /// regex/format drift between tests and implementation.
    /// </summary>
    private static List<BlackFrame> ParseFingerprintFile(string filename)
    {
        var path = Path.Combine("..", "..", "..", "fingerprints", filename);
        var raw = File.ReadAllText(path);
        return [.. FFmpegWrapper.ParseBlackFrame(raw)];
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

    private static BlackFrameAnalyzer CreateBlackFrameAnalyzer()
    {
        var logger = new LoggerFactory().CreateLogger<BlackFrameAnalyzer>();
        return new(logger);
    }
}
