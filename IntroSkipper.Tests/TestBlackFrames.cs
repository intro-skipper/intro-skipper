// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
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
using static IntroSkipper.Tests.BlackFrameFixtures;
using static IntroSkipper.Tests.StubFFmpegService;

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

    /// <summary>
    /// A 10-bit gray source at luma 80 (20 on the 8-bit scale) is black at threshold 28 when
    /// blackframe reads it as gray, and not black once converted to limited-range yuv420p,
    /// where the same luma lands at 33. The keyframe scan's visuals filters must not change
    /// what blackframe sees, regardless of whether a given FFmpeg build emits two or three
    /// 1 fps keyframes for this synthetic clip.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task DetectBlackFramesAsync_KeepsBlackFrameFormatNegotiationOnGraySources()
    {
        var path = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-gray10.mkv");
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            ["-y", "-v", "error", "-f", "lavfi", "-i", "color=c=#141414:s=64x64:r=1:d=3", "-pix_fmt", "gray10le", "-c:v", "ffv1", path]);
        try
        {
            var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "gray10", Path = path, Duration = 3 };
            var ffmpegService = FfmpegTestHelpers.CreateFFmpegService();

            var frames = await ffmpegService.DetectBlackFramesAsync(episode, 28);
            var (start, end) = episode.GetFingerprintRange(AnalysisMode.Credits);
            var keyframes = await ffmpegService.DetectKeyFramesAsync(episode, new(start, end - start), AnalysisMode.Credits);

            Assert.NotEmpty(keyframes);
            Assert.Equal(keyframes.Length, frames.Length);
            Assert.All(frames, frame => Assert.Equal(100, frame.Percentage));
        }
        finally
        {
            File.Delete(path);
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
        Assert.All(visuals, v => Assert.True(v.LumaLow <= v.LumaHigh)); // real percentiles parsed
        Assert.All(visuals, v => Assert.All([v.LumaMin, v.LumaLow, v.LumaHigh, v.LumaMax], luma => Assert.InRange(luma, 0, 255)));
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
        // rescued by blackdetect interval confirmation in KeyframeAnalyzer, not by relaxing
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

    [Theory]
    [InlineData("black", 120.0)]
    [InlineData("tinted", null)]
    [InlineData("blank", null)]
    [InlineData("one page", null)]
    [InlineData("most pages", 120.0)]
    [InlineData("half the pages", null)]
    [InlineData("none", 120.0)]
    [InlineData("dense text", 120.0)]
    [InlineData("red text", 120.0)]
    [InlineData("dense red text", 120.0)]
    [InlineData("dark highlight", 120.0)]
    [InlineData("cut then highlight", null)]
    [InlineData("dark grey lead-in", 130.0)]
    [InlineData("lifted blacks", 120.0)]
    [InlineData("mixed black levels", 134.0)]
    [InlineData("letterboxed dark lead-in", 130.0)]
    [InlineData("letterboxed dark majority", 138.0)]
    [InlineData("big text", 120.0)]
    [InlineData("dense first pages", 124.0)]
    public async Task DetectCreditsAsync_GatesBlackScenesOnTheirVisuals(string visualsKind, double? expectedStart)
    {
        // A dense black roll at 20 to 54. Its visuals decide: text pages are a roll; saturated
        // pages are a dark tinted scene, not black at all; blank pages are a gap between acts, not
        // credits, until lettering shows on more than half of them; no visuals at all leave the
        // black-frame result alone. Dense lettering and coloured lettering are rolls like any other.
        // A dark scene with one lit spot on every page is a roll as it always was; a cut followed by
        // such a keyframe is lettered on half its pages and is not. A dark grey lead-in, black to the
        // blackframe filter with its darkest tenth above the roll's, is not part of the roll; a roll
        // with lifted blacks sets the scene's black level and is a roll; a roll authored at two black
        // levels starts at its darker part. Behind letterbox bars a dark scene's darkest tenth is
        // black like the roll's, and its 90th percentile gives it away, however long it runs; large
        // lettering lifts a roll page's 90th percentile onto the text and is a roll. Pages dense
        // enough to lift the 90th percentile into the dark band read as dim content, so a roll that
        // opens on them starts after them. No frames are decoded here, so every lead-in takes the
        // policy's keyframe start; the lead-in probe's own tests cover the frame it moves to. A page
        // whose 90th percentile reaches the threshold of 32 is at most nine tenths black, so dense and
        // large lettering and the pages behind bars sit at 89, black at the floor of 30 that a scan
        // of the roll alone keeps. No fixture gives a card run: an accepted roll's pages are black
        // cards, and the pages of a rejected or tinted one are content.
        Keyframe[] keyframes = visualsKind switch
        {
            "black" => Roll((20, 54, 95, KeyframeVisuals.Black)),
            "tinted" => Roll((20, 54, 95, KeyframeVisuals.Tinted)),
            "blank" => Roll((20, 54, 95, KeyframeVisuals.BlankBlack)),
            "one page" => Roll((30, 30, 95, KeyframeVisuals.Black), (20, 54, 95, KeyframeVisuals.BlankBlack)),
            "most pages" => Roll((20, 54, 95, t => (t - 20) % 1.5 == 0 ? KeyframeVisuals.BlankBlack(t) : KeyframeVisuals.Black(t))),
            "half the pages" => Roll((20, 54, 95, t => (t - 20) % 1 == 0 ? KeyframeVisuals.BlankBlack(t) : KeyframeVisuals.Black(t))),
            "none" => Roll((20, 54, 95, _ => null)),
            "dense text" => Roll((20, 54, 89, KeyframeVisuals.DenseText)),
            "red text" => Roll((20, 54, 95, KeyframeVisuals.RedText)),
            "dense red text" => Roll((20, 54, 89, KeyframeVisuals.DenseRedText)),
            "dark highlight" => Roll((20, 54, 95, KeyframeVisuals.DarkHighlight)),
            "cut then highlight" => Roll((20, 37, 95, KeyframeVisuals.BlankBlack), (37.5, 54, 95, KeyframeVisuals.DarkHighlight)),
            "dark grey lead-in" => Roll((20, 29.5, 95, KeyframeVisuals.DarkGrey), (30, 54, 95, KeyframeVisuals.Black)),
            "lifted blacks" => Roll((20, 54, 95, KeyframeVisuals.LiftedBlack)),
            "mixed black levels" => Roll((20, 33.5, 95, KeyframeVisuals.LiftedBlack), (34, 54, 95, KeyframeVisuals.Black)),
            "letterboxed dark lead-in" => Roll((20, 29.5, 89, KeyframeVisuals.LetterboxedDark), (30, 54, 95, KeyframeVisuals.Black)),
            "letterboxed dark majority" => Roll((20, 37.5, 89, KeyframeVisuals.LetterboxedDark), (38, 54, 95, KeyframeVisuals.Black)),
            "big text" => Roll((20, 54, 89, KeyframeVisuals.BigText)),
            "dense first pages" => Roll((20, 23.5, 89, KeyframeVisuals.DenseText), (24, 54, 95, KeyframeVisuals.Black)),
            _ => throw new ArgumentOutOfRangeException(nameof(visualsKind)),
        };
        var ffmpeg = KeyframeScan(keyframes);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        (SegmentSource, double, double)[] expected = expectedStart is { } start ? [(SegmentSource.BlackFrame, start, 154)] : [];
        Assert.Equal(expected, candidates);

        static Keyframe[] Roll(params (double From, double To, int Percentage, Func<double, KeyframeVisual?> Visual)[] spans)
            => Keyframes(20, 54, 0.5, spans);
    }

    [Fact]
    public async Task DetectCreditsAsync_LeadInProbeTrim_StartsOnTheFrame()
    {
        // Keyframes two seconds apart, a dark grey lead-in nominated between 28 and 30. The probe
        // reads from the lighter keyframe to just past the level one at its width; the lit object
        // vanishes into blank black at 129, between the keyframes, and the scene starts there. The
        // lead-in is a rejected range, so its card-like keyframes are content and the black-frame
        // candidate is the only one.
        var ffmpeg = KeyframeScan(
            Keyframes(20, 54, 2, (20, 28, 95, KeyframeVisuals.DarkGrey), (30, 54, 95, KeyframeVisuals.Black)),
            lumaWindows: (_, window, _) => LumaWindows.Window(window.Start, (129 - window.Start, () => LumaWindows.Blob(21)), (window.End - 129, () => LumaWindows.Blank(16))));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 129.0, 154.0)], candidates);
        Assert.Equal([new LumaDecode(128, 130.25, 160)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_LeadInProbeDecodesEachTrimmedScene()
    {
        // Two black scenes with a dark grey lead-in each and content between them, too far apart to
        // merge. Every trimmed scene gets the lead-in probe, so both windows are decoded. In the later
        // window the lit object vanishes at 165 and starts the scene there. The earlier window shows
        // the lit object throughout, the keyframe's own frame included, so every frame matches the
        // keyframe and its scene starts on the first frame after the lighter keyframe at 120.
        var ffmpeg = KeyframeScan(
            Keyframes(20, 94, 2, (20, 20, 95, KeyframeVisuals.DarkGrey), (22, 40, 95, KeyframeVisuals.Black), (64, 64, 95, KeyframeVisuals.DarkGrey), (66, 94, 95, KeyframeVisuals.Black)),
            lumaWindows: (_, window, _) => LumaWindows.Window(window.Start, (165 - window.Start, () => LumaWindows.Blob(21)), (window.End - 165, () => LumaWindows.Blank(16))));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 120.042, 140.0), (SegmentSource.BlackFrame, 165.0, 194.0)], candidates.Select(c => (c.Source, Math.Round(c.Start, 3), c.End)));
        Assert.Equal([new LumaDecode(120, 122.25, 160), new LumaDecode(164, 166.25, 160)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_LeadInProbeStartIsCached()
    {
        // The start the probe locates is a function of the file between the two keyframes, so a
        // second analysis of the episode reads it from the detection cache instead of decoding.
        var ffmpeg = KeyframeScan(
            Keyframes(20, 54, 2, (20, 28, 95, KeyframeVisuals.DarkGrey), (30, 54, 95, KeyframeVisuals.Black)),
            lumaWindows: (_, window, _) => LumaWindows.Window(window.Start, (129 - window.Start, () => LumaWindows.Blob(21)), (window.End - 129, () => LumaWindows.Blank(16))));
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true }, cache);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var first = await KeyframeCandidates(analyzer, episode);
        var second = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 129.0, 154.0)], first);
        Assert.Equal(first, second);
        Assert.Equal([new LumaDecode(128, 130.25, 160)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_WithoutBoundaryRefinement_DecodesNoLeadIn()
    {
        // Keyframe-only analysis: the lead-in still trims to the keyframe at 30, and nothing between
        // the keyframes is decoded, though the frames there would move the start to 129.
        var ffmpeg = KeyframeScan(
            Keyframes(20, 54, 2, (20, 28, 95, KeyframeVisuals.DarkGrey), (30, 54, 95, KeyframeVisuals.Black)),
            lumaWindows: (_, window, _) => LumaWindows.Window(window.Start, (129 - window.Start, () => LumaWindows.Blob(21)), (window.End - 129, () => LumaWindows.Blank(16))));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 130.0, 154.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_TrimmedSceneKeepsItsKeyframeStart()
    {
        // Keyframes two seconds apart, a dark grey lead-in nominated between 28 and 30 and a lead-in
        // decode that fails, so the policy trims at 30. The boundary probe would read the gap before
        // 30 with blackframe, which scores the lead-in black, and pull the start back into it; a
        // trimmed scene does not probe.
        var ffmpeg = KeyframeScan(
            Keyframes(20, 54, 2, (20, 28, 95, KeyframeVisuals.DarkGrey), (30, 54, 95, KeyframeVisuals.Black)),
            probeFrames: [new BlackFrame(100, 0.2, 5)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 130.0, 154.0)], candidates);
        Assert.Equal([new LumaDecode(128, 130.25, 160)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_TrimmedLeadInDoesNotComeBackAsCards()
    {
        // A card-like dark grey lead-in of 12 s before 13 s of roll: the trim leaves the scene under
        // the minimum, so there is no accepted scene and the card finder's visual-only fallback runs.
        // The lead-in must be content there, or it returns as a card run over lead-in and roll.
        var ffmpeg = KeyframeScan(Keyframes(20, 45, 0.5, (20, 31.5, 95, KeyframeVisuals.DarkGrey), (32, 45, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("level", 40.0, true, false)]
    [InlineData("no visual", 40.0, false, false)]
    [InlineData("dim", 50.0, false, true)]
    [InlineData("level before dim", 40.0, true, false)]
    [InlineData("content", 40.0, false, false)]
    public async Task DetectCreditsAsync_PageJustBeforeAnIntervalStart_IsTheScenesFirstPage(string page, double blackFrameStart, bool pageIsBlackCard, bool leadIn)
    {
        // A sparse roll that a blackdetect interval confirms starts on blackdetect's clock, at 40.
        // The page 5 ms before it is the cut keyframe on the scan's clock and the scene's first page,
        // so each fixture gives the same candidates as its twin with the page at 40, except that a
        // card run that starts on the page starts 5 ms earlier. The interval probe covers the roll
        // from its first black page, padded by the minimum. A level page, or one without a visual,
        // keeps the start at 40 and runs no boundary probe, since the gap from the page to 40 is
        // under the boundary probe's minimum window. A dim page is a lead-in. The scene starts at the
        // level start frame at 50, and the lead-in probe decodes the frames between the two. A dim
        // start frame after a level page stays in the scene. With a content keyframe in the page's
        // place, the walk starts on the start frame at 50, and the scene keeps 40 and runs no probe.
        // The card run covers the roll and the grey cards after it, and starts on the page when the
        // scene keeps it as a black card.
        var ffmpeg = PageBeforeTheRoll(page, 39.995);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        var firstBlackPage = page == "content" ? 50 : 39.995;
        Call[] probes = leadIn
            ? [new IntervalScan(firstBlackPage - 15, 95, 32, 85), new LumaDecode(39.995, 50.25, 160)]
            : [new IntervalScan(firstBlackPage - 15, 95, 32, 85)];
        Assert.Equal(Expected(39.995), candidates);
        Assert.Equal(probes, ffmpeg.Calls);

        // The twin puts the page at blackdetect's start, where it is the start frame. Refinement is
        // off because there a scene without a lead-in runs the boundary probe over the gap from the
        // keyframe at 30.
        var twinFfmpeg = PageBeforeTheRoll(page, 40);
        var twin = CreateKeyframeAnalyzer(twinFfmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var twinCandidates = await KeyframeCandidates(twin, CreateQueuedCreditsEpisode());

        Assert.Equal(Expected(40), twinCandidates);
        Assert.Equal([new IntervalScan(page == "content" ? 35 : 25, 95, 32, 85)], twinFfmpeg.Calls);

        (SegmentSource Source, double Start, double End)[] Expected(double pageTime) =>
            [(SegmentSource.BlackFrame, blackFrameStart, 80), (SegmentSource.KeyframeVisuals, pageIsBlackCard ? pageTime : 50, 120)];
    }

    [Fact]
    public async Task DetectCreditsAsync_LighterTailStaysInTheScene()
    {
        // The black-level rule is confined to the leading boundary: a roll whose last pages sit on a
        // lifted black keeps them, since only a lead-in is ambiguous with story.
        var ffmpeg = KeyframeScan(Keyframes(20, 54, 0.5, (20, 39.5, 95, KeyframeVisuals.Black), (40, 54, 95, KeyframeVisuals.LiftedBlack)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 120.0, 154.0)], candidates);
    }

    [Fact]
    public async Task DetectCreditsAsync_SceneWithoutVisualEvidenceIsKept()
    {
        // A roll whose keyframes the visuals decode did not report, after content keyframes it did.
        // The gates find no visual on any of the roll's pages and must leave the black-frame result
        // alone rather than reject the roll on a zero count.
        var ffmpeg = KeyframeScan(Keyframes(0, 54, 0.5, (20, 54, 95, _ => null)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 120.0, 154.0)], candidates);
    }

    [Fact]
    public async Task DetectCreditsAsync_RejectedGapEndpointsWithRoundedTimesStayRejected()
    {
        // The two ffmpeg filters print the same keyframe with different rounding. The rejected
        // scene's bounds come from the black-frame times, so its lettered endpoints, whose visuals
        // land a fraction of a millisecond outside those bounds, must still be rejected pages and
        // not become a card run of their own.
        Keyframe[] keyframes =
        [
            new(20.000019, 95, KeyframeVisuals.Black(20)),
            new(25, 95, KeyframeVisuals.BlankBlack(25)),
            new(30, 95, KeyframeVisuals.BlankBlack(30)),
            new(35.000099, 95, KeyframeVisuals.Black(35.0001)),
        ];
        var ffmpeg = KeyframeScan(keyframes);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("one page")]
    [InlineData("half the pages")]
    [InlineData("cut then highlight")]
    public async Task DetectCreditsAsync_RejectedGapDoesNotComeBackAsCards(string visualsKind)
    {
        // The lettering gate rejects the scene; its black pages must not reach the card finder's
        // no-scene fallback and come back as a card run.
        var keyframes = visualsKind switch
        {
            "one page" => Keyframes(20, 54, 0.5, (30, 30, 95, KeyframeVisuals.Black), (20, 54, 95, KeyframeVisuals.BlankBlack)),
            "half the pages" => Keyframes(20, 54, 0.5, (20, 54, 95, t => (t - 20) % 1 == 0 ? KeyframeVisuals.BlankBlack(t) : KeyframeVisuals.Black(t))),
            _ => Keyframes(20, 54, 0.5, (20, 37, 95, KeyframeVisuals.BlankBlack), (37.5, 54, 95, KeyframeVisuals.DarkHighlight)),
        };
        var ffmpeg = KeyframeScan(keyframes);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData(1, 81, true)]
    [InlineData(9, 81, true)]
    [InlineData(17, 81, true)]
    [InlineData(29, 81, true)]
    [InlineData(58, 81, true)]
    [InlineData(65, 81, true)]
    [InlineData(0, 244, true)]
    [InlineData(0, 75, false)]
    [InlineData(0, 16, false)]
    public void TestIsLetteredPage_AnyDensityAndBrightness(double spread, double max, bool expected)
    {
        // Red lettering on black swept across font sizes: the 90th percentile climbs from the
        // background onto the text as the lettering thickens, and every size is lettering. Only the
        // contrast decides: a page whose brightest pixel is under 60 levels above the darkest tenth is blank.
        var visual = new KeyframeVisual(0, 16, 16, 16 + spread, max, 0, 11);

        Assert.Equal(expected, CardRunFinder.IsLetteredPage(visual));
    }

    [Fact]
    public async Task DetectCreditsAsync_TintedKeyframesAreNeitherBlackNorCards()
    {
        // Saturated black keyframes flat enough to pass the card test on luma alone: not a black
        // scene, and not a card run through the no-scene fallback either.
        var ffmpeg = KeyframeScan(Keyframes(20, 54, 0.5, (20, 54, 95, KeyframeVisuals.TintedFlat)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task DetectCreditsAsync_MultipleSparseScenesRequireBlackIntervalSupport()
    {
        // Two sparse scenes, each after a content keyframe, clear the keyframe density gate because
        // the source has only a few keyframes in each run. The large gap separates the runs; the
        // interval probe runs over each scene padded by the minimum, and only the first scene has a
        // confirmed blackdetect interval and should remain a credits candidate.
        Keyframe[] keyframes =
        [
            new(0, 10, KeyframeVisuals.Content(0)),
            .. Keyframes(10, 30, 10, (10, 30, 96, KeyframeVisuals.Black)),
            new(100, 10, KeyframeVisuals.Content(100)),
            .. Keyframes(110, 130, 10, (110, 130, 96, KeyframeVisuals.Black)),
        ];
        var ffmpeg = KeyframeScan(keyframes, intervals: [new BlackInterval(10, 30)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 10.0, 30.0)], candidates);
        Assert.Equal([new IntervalScan(0, 45, 32, 86), new IntervalScan(95, 145, 32, 86)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_UnconfirmedProbePreservesKeyframeScenes()
    {
        // The same two sparse scenes and an interval that confirms neither: the probe changes
        // nothing, and both scenes are candidates.
        Keyframe[] keyframes =
        [
            new(0, 10, KeyframeVisuals.Content(0)),
            .. Keyframes(10, 30, 10, (10, 30, 96, KeyframeVisuals.Black)),
            new(100, 10, KeyframeVisuals.Content(100)),
            .. Keyframes(110, 130, 10, (110, 130, 96, KeyframeVisuals.Black)),
        ];
        var ffmpeg = KeyframeScan(keyframes, intervals: [new BlackInterval(0, 1)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        Assert.Equal([(SegmentSource.BlackFrame, 10.0, 30.0), (SegmentSource.BlackFrame, 110.0, 130.0)], candidates);
        Assert.Equal([new IntervalScan(0, 45, 32, 86), new IntervalScan(95, 145, 32, 86)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_SparseProbePreservesDenseSceneAndItsCardRun()
    {
        // A sparse scene that an interval confirms, then grey cards and a dense roll. The probe
        // promotes the sparse scene and keeps the dense one as it is, so both are candidates, and
        // the card run covers the cards and the roll after them.
        Keyframe[] keyframes =
        [
            new(0, 0, KeyframeVisuals.Content(0)),
            .. Keyframes(10, 30, 10, (10, 30, 96, KeyframeVisuals.Black)),
            new(60, 0, KeyframeVisuals.Content(60)),
            .. Keyframes(90, 98, 2, (90, 98, 0, t => KeyframeVisuals.Card(t))),
            .. Keyframes(100, 130, 2, (100, 130, 96, KeyframeVisuals.Black)),
        ];
        var ffmpeg = KeyframeScan(keyframes, intervals: [new BlackInterval(10, 30)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        Assert.Equal([(SegmentSource.BlackFrame, 10.0, 30.0), (SegmentSource.BlackFrame, 100.0, 130.0), (SegmentSource.KeyframeVisuals, 90.0, 130.0)], candidates);
        Assert.Equal([new IntervalScan(0, 45, 32, 85), new IntervalScan(85, 145, 32, 85)], ffmpeg.Calls);
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
        var ffmpeg = KeyframeScan([]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
        Assert.Equal(1, ffmpeg.CreditsScanCalls);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_SingleCleanScene_ReturnsOffsetSegment()
    {
        var ffmpeg = KeyframeScan(Keyframes(0, 20, 0.5, (0, 20, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 100.0, 120.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_ReturnsBlackFrameAndCardCandidates()
    {
        // Keyframes every 2 s, the cadence of the card visuals: grey cards to 18, then a black roll
        // from 20 to 54. The boundary probe reads the 2 s from the last card to the roll.
        var ffmpeg = KeyframeScan(Keyframes(0, 54, 2, (0, 18, 0, t => KeyframeVisuals.Card(t)), (20, 54, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 120.0, 154.0), (SegmentSource.KeyframeVisuals, 100.0, 154.0)], candidates);
        Assert.Equal([new RangeScan(118, 120, 95, 32, AnalysisMode.Credits)], ffmpeg.Calls);
        Assert.Equal(1, ffmpeg.CreditsScanCalls);
        Assert.Equal(1, ffmpeg.VisualScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_TooShortScene_ReturnsNull()
    {
        var ffmpeg = KeyframeScan(Keyframes(0, 10, 0.5, (0, 10, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
        Assert.Equal([new IntervalScan(0, 25, 32, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_DarkLowDensityScene_ReturnsNull()
    {
        var ffmpeg = KeyframeScan(DarkSceneWithBlackPages(every: 5, percentage: 95));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
        Assert.Equal([new IntervalScan(0, 62.5, 32, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_LowDensitySingleCandidateUsesIntervalSupport()
    {
        var ffmpeg = KeyframeScan(
            DarkSceneWithBlackPages(every: 3, percentage: 90),
            intervals: [new BlackInterval(1, 49)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 1.0, 49.5)], candidates);
        Assert.Equal([new IntervalScan(0, 64.5, 32, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_LowDensitySingleCandidateWithoutIntervalSupportReturnsNull()
    {
        var ffmpeg = KeyframeScan(DarkSceneWithBlackPages(every: 3, percentage: 90));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
        Assert.Equal([new IntervalScan(0, 64.5, 32, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_StingerSplit_ReturnsBothParts()
    {
        var ffmpeg = KeyframeScan(Keyframes(0, 120, 0.5, (0, 20, 95, KeyframeVisuals.Black), (20.5, 89.5, 30, KeyframeVisuals.Dark), (90, 120, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 1000);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 1000.0, 1020.0), (SegmentSource.BlackFrame, 1090.0, 1120.0)], candidates);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_ValidBlackFrameSceneSkipsIntervalPromotion()
    {
        var ffmpeg = KeyframeScan(
            Keyframes(0, 120, 0.5, (0, 20, 95, KeyframeVisuals.Black), (20.5, 89.5, 30, KeyframeVisuals.Dark), (90, 120, 95, KeyframeVisuals.Black)),
            intervals: [new BlackInterval(5, 10)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 1000);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 1000.0, 1020.0), (SegmentSource.BlackFrame, 1090.0, 1120.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_BlackIntervalsRecoverSparseKeyframeCredits()
    {
        var ffmpeg = KeyframeScan(
            [
                new(366.45, 15, KeyframeVisuals.Dark(366.45)),
                new(376.46, 96, KeyframeVisuals.Black(376.46)),
                new(386.47, 96, KeyframeVisuals.Black(386.47)),
                new(396.48, 98, KeyframeVisuals.Black(396.48)),
                new(406.49, 99, KeyframeVisuals.Black(406.49)),
                new(416.5, 20, KeyframeVisuals.Dark(416.5)),
            ],
            intervals: [new BlackInterval(367.827, 376.002)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 2356.27);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 2356.27 + 367.827, 2356.27 + 406.49)], candidates);
        Assert.Equal(
            [
                new IntervalScan(2356.27 + 376.46 - 15, 2356.27 + 406.49 + 15, 32, 87),
                new RangeScan(2356.27 + 366.45, 2356.27 + 376.46, 95, 32, AnalysisMode.Credits),
            ],
            ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_SparseSingleSceneWithoutIntervalSupportIsStillReturned()
    {
        // A single scene that already clears the density and duration gates but is temporally sparse
        // triggers an opportunistic blackdetect probe. When that probe finds no supporting interval the
        // scene is kept, not rejected: sparsity drives optional refinement, it is not a trust gate. This
        // is the deliberate counterpart to the count==0 candidate path, which does require interval support.
        var ffmpeg = KeyframeScan(Keyframes(0, 50, 10, (10, 40, 96, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 10.0, 40.0)], candidates);

        // The opportunistic interval probe ran but returned nothing; the keyframe scene survives the miss.
        Assert.Equal([new IntervalScan(0, 55, 32, 85)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_BlackIntervalsExpandSingleShortScene()
    {
        var ffmpeg = KeyframeScan(
            Keyframes(10, 20, 2, (10, 20, 96, KeyframeVisuals.Black)),
            intervals: [new BlackInterval(5, 19.8)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 105.0, 120.0)], candidates);
        Assert.Equal([new IntervalScan(100, 135, 32, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_RunsOneIntervalConfirms_AreOneScene()
    {
        // Tinted pages at 80 and 90 split a sparse roll into keyframe runs at 40 to 70 and 100 to
        // 150, and one blackdetect interval from 40 confirms both. They are one scene, so its black
        // level comes from all its pages and the lifted pages at its head are a lead-in. The lead-in
        // decode fails, so the scene starts at the level start frame at 100, and there is one
        // candidate.
        var ffmpeg = KeyframeScan(
            Keyframes(0, 200, 10, (40, 70, 100, KeyframeVisuals.LiftedBlack), (80, 90, 100, KeyframeVisuals.Tinted), (100, 150, 100, KeyframeVisuals.Black)),
            intervals: [new BlackInterval(40, 110)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        Assert.Equal([(SegmentSource.BlackFrame, 100.0, 150.0)], candidates);
        Assert.Equal([new IntervalScan(25, 165, 32, 85), new LumaDecode(70, 100.25, 160)], ffmpeg.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetectCreditsAsync_DenseSceneUnderAnInterval_KeepsItsRuns(bool oneInterval)
    {
        // A dense roll at 20 to 40 and a sparse run at 70 to 90. The dense scene keeps its own start
        // and its runs, and an interval makes a scene only of the runs outside it. One interval over
        // both, through tinted pages between them, gives a second scene from 19, which the credits
        // pass joins to the first. An interval over each gives a second scene from 65, and no copy
        // of the dense scene from 19.
        double[] times = [.. Times(0, 62, 2), 70, 80, 90, .. Times(92, 120, 2)];
        var ffmpeg = KeyframeScan(
            times.Select(t => t switch
            {
                >= 20 and <= 40 or >= 70 and <= 90 => new Keyframe(t, 100, KeyframeVisuals.Black(t)),
                > 40 and < 70 when oneInterval => new Keyframe(t, 100, KeyframeVisuals.Tinted(t)),
                _ => new Keyframe(t, 0, KeyframeVisuals.Content(t)),
            }),
            intervals: oneInterval ? [new BlackInterval(19, 90)] : [new BlackInterval(19, 40), new BlackInterval(65, 90)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        (SegmentSource, double, double)[] expected = oneInterval
            ? [(SegmentSource.BlackFrame, 20, 40), (SegmentSource.BlackFrame, 19, 90)]
            : [(SegmentSource.BlackFrame, 20, 40), (SegmentSource.BlackFrame, 65, 90)];
        Assert.Equal(expected, candidates);
        Assert.Equal([new IntervalScan(5, 105, 32, 85)], ffmpeg.Calls);
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
            CreditSceneBuilder.FindRawScenes([.. frames], 85),
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
            CreditSceneBuilder.FindRawScenes(frames, 85),
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
            CreditSceneBuilder.FindRawScenes([.. frames], 85),
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
        var ffmpeg = KeyframeScan(
            [
                new(366.45, 15, KeyframeVisuals.Dark(366.45)),
                new(376.46, 20, KeyframeVisuals.Dark(376.46)),
                new(386.47, 18, KeyframeVisuals.Dark(386.47)),
                new(396.48, 22, KeyframeVisuals.Dark(396.48)),
            ],
            intervals: [new BlackInterval(367.827, 376.002)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 2356.27);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
        Assert.Empty(ffmpeg.Calls);
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
        var ranges = KeyframeAnalyzer.BuildIntervalProbeRanges(
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

    [Fact]
    public async Task TestDetectCreditsAsync_RefinesBoundaryByDefault()
    {
        var ffmpeg = KeyframeScan(
            [.. Keyframes(0, 8, 0.5, (0, 8, 30, KeyframeVisuals.Dark)), .. Keyframes(10, 30, 0.5, (10, 30, 95, KeyframeVisuals.Black))],
            probeFrames: [new BlackFrame(95, 1.25, 0)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 109.25, 130.0)], candidates);
        Assert.Equal([new RangeScan(108, 110, 95, 32, AnalysisMode.Credits)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_RefinesSubMinimumFinalSceneBesideEarlierScene()
    {
        var ffmpeg = KeyframeScan(
            [.. Keyframes(0, 58, 0.5, (0, 20, 95, KeyframeVisuals.Black), (20.5, 58, 30, KeyframeVisuals.Dark)), .. Keyframes(60, 74, 0.5, (60, 74, 95, KeyframeVisuals.Black))],
            probeFrames: [new BlackFrame(95, 0.5, 0)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 100.0, 120.0), (SegmentSource.BlackFrame, 158.5, 174.0)], candidates);
        Assert.Equal([new RangeScan(158, 160, 95, 32, AnalysisMode.Credits)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_EarlierSceneUnderTheMinimumAfterProbing_IsNotACandidate()
    {
        // 10 to 23 is 13 s, admitted because the 2 s keyframe gap before it could bring it to the
        // minimum, and 60 to 80 is a roll. Every accepted scene is probed. The probe finds no black
        // before 10, so that scene stays under the minimum and the roll is the only candidate.
        var ffmpeg = KeyframeScan(
        [
            .. Keyframes(0, 8, 0.5, (0, 8, 30, KeyframeVisuals.Dark)),
            .. Keyframes(10, 58, 0.5, (10, 23, 95, KeyframeVisuals.Black), (23.5, 58, 30, KeyframeVisuals.Dark)),
            .. Keyframes(60, 80, 0.5, (60, 80, 95, KeyframeVisuals.Black)),
        ]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 160.0, 180.0)], candidates);
        Assert.Equal([new RangeScan(108, 110, 95, 32, AnalysisMode.Credits), new RangeScan(158, 160, 95, 32, AnalysisMode.Credits)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_DisabledBoundaryRefinement_UsesKeyframeStart()
    {
        var ffmpeg = KeyframeScan(
            [.. Keyframes(0, 8, 0.5, (0, 8, 30, KeyframeVisuals.Dark)), .. Keyframes(10, 30, 0.5, (10, 30, 95, KeyframeVisuals.Black))],
            probeFrames: [new BlackFrame(95, 1.25, 0)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 110.0, 130.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_DisabledRefinement_DoesNotSuppressIntervalFallback()
    {
        // The only keyframe scene is too short on its own and could reach the minimum duration only via
        // boundary refinement. With refinement disabled it must not be admitted, so the interval fallback
        // can still recover the credits instead of the analyzer returning null.
        var ffmpeg = KeyframeScan(
            [.. Keyframes(0, 8, 0.5, (0, 8, 30, KeyframeVisuals.Dark)), .. Keyframes(14, 24, 0.5, (14, 24, 95, KeyframeVisuals.Black))],
            intervals: [new BlackInterval(8, 24)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 108.0, 124.0)], candidates);
        Assert.Equal([new IntervalScan(100, 139, 32, 89)], ffmpeg.Calls);
    }

    /// <summary>
    /// Keyframes 0.5 s apart from 0 to 49.5 in a dark scene at 30 percent black, with a black roll
    /// page at <paramref name="percentage"/> on every <paramref name="every"/>th keyframe from 0.
    /// </summary>
    private static IEnumerable<Keyframe> DarkSceneWithBlackPages(int every, int percentage)
        => Times(0, 49.5, 0.5).Select((t, i) => i % every == 0 ? new Keyframe(t, percentage, KeyframeVisuals.Black(t)) : new Keyframe(t, 30, KeyframeVisuals.Dark(t)));

    [Fact]
    public async Task DetectCreditsAsync_DimFirstPageBeforeAPageWithoutAVisual_IsTheLeadIn()
    {
        // The roll's first page is dim and its second page has no visual. A page without a visual
        // ends the lead-in walk, since nothing says it is dim. So the dim page alone is the lead-in,
        // and the scene starts on the page without a visual at 22, not on the first level page at
        // 24. The lead-in decode between the two fails, so the start stays on that page.
        var ffmpeg = KeyframeScan(Keyframes(0, 70, 2, (20, 20, 95, KeyframeVisuals.DarkGrey), (22, 22, 95, _ => null), (24, 54, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.BlackFrame, 122.0, 154.0)], candidates);
        Assert.Equal([new LumaDecode(120, 122.25, 160)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_DenseTextWithoutAnAcceptedScene_IsContent()
    {
        // Grey cards run for 10 s, then dense lettering on black at 88 percent runs for 12 s. That is
        // too short for a black scene and blackdetect confirms nothing, so no scene is accepted and
        // the card finder falls back to the visuals. Dense text is lettered but not card-like, so
        // the finder reads it as content, not as a card or a black card. The cards alone are under
        // the minimum.
        var ffmpeg = KeyframeScan(Keyframes(0, 70, 2, (30, 40, 0, t => KeyframeVisuals.Card(t)), (42, 54, 88, KeyframeVisuals.DenseText)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Empty(candidates);
        Assert.Equal([new IntervalScan(127, 169, 32, 85)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_IntervalSupportedBlankScene_IsARejectedGap()
    {
        // A blank black gap between acts has keyframes ten seconds apart, and two of its five pages
        // are lettered. The gap is sparse, so the analyzer probes it, and the interval from 35 makes
        // it a scene that starts at blackdetect's start. The lettering gate still reads that scene
        // and rejects it, so there is no black-frame candidate. Its lettered pages are black and
        // inside the rejected range, so they are content and do not become a card run from 50 to 70.
        var ffmpeg = KeyframeScan(
            Keyframes(0, 120, 10, (50, 50, 100, KeyframeVisuals.Black), (70, 70, 100, KeyframeVisuals.Black), (40, 80, 100, KeyframeVisuals.BlankBlack)),
            intervals: [new BlackInterval(35, 80)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        Assert.Empty(candidates);
        Assert.Equal([new IntervalScan(25, 95, 32, 85)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_DarkScanWithTintedPages_TakesItsFloorFromThem()
    {
        // In this dark scan the blackframe filter scores every page at 30 percent or more, except
        // two dark tinted pages at 95. The analyzer counts tinted pages as zero before it normalizes
        // the thresholds, so they alone pull the floor to 0 and keep the black minimum at the
        // configured 85. Without them the floor would be 30 and the minimum 89. The roll of large
        // lettering at 87 percent sits between the two, so only the tinted rows make it a scene. The
        // boundary probe runs at its start page's 87.
        var ffmpeg = KeyframeScan(Keyframes(0, 100, 2, (10, 12, 95, KeyframeVisuals.Tinted), (60, 90, 87, KeyframeVisuals.BigText), (0, 100, 50, KeyframeVisuals.Dark)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.BlackFrame, 160.0, 190.0)], candidates);
        Assert.Equal([new RangeScan(158, 160, 87, 32, AnalysisMode.Credits)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_TintedPageBeforeTheTransitionRow_IsNotTheStart()
    {
        // Letterboxed dark shots at 88 percent come first, then a flat tinted page at 100, then the
        // roll. The black path reads the tinted page as not black, in the transition shift too. So
        // the scene starts at the roll's first page at 32, and the boundary probe reads the gap from
        // the tinted page. The card finder reads the tinted page's raw percentage, and a tinted page
        // that is black is content, so the flat tinted page does not start a card run before the roll.
        var ffmpeg = KeyframeScan(Keyframes(0, 80, 2, (20, 28, 88, KeyframeVisuals.LetterboxedDark), (30, 30, 100, KeyframeVisuals.TintedFlat), (32, 60, 100, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.BlackFrame, 132.0, 160.0)], candidates);
        Assert.Equal([new RangeScan(130, 132, 95, 32, AnalysisMode.Credits)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_SceneOnlyPastTheWindowEnd_LeavesNoCardFallback()
    {
        // The credits window ends 60 s in. The black rows run to the end of the file and have no
        // visuals past the window. A roll that lies only there, from 82 to 110, is an accepted scene,
        // since without visuals it has no lead-in and no lettering verdict. Inside the window, grey
        // cards and then 12 s of black roll pages are too short for a black scene. Accepted is not
        // empty, so the card finder does not fall back to the visuals. The black pages outside every
        // accepted scene are content, the cards alone are under the minimum, and there is no card run.
        var ffmpeg = KeyframeScan(Keyframes(
            0,
            120,
            2,
            (32, 40, 0, t => KeyframeVisuals.Card(t)),
            (42, 54, 100, KeyframeVisuals.Black),
            (82, 110, 100, _ => null),
            (62, 120, 0, _ => null)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Name = "episode.mkv",
            Path = "episode.mkv",
            Duration = 220,
            CreditsFingerprintStart = 100,
            CreditsFingerprintEnd = 160,
        };

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 182.0, 210.0)], candidates);
        Assert.Equal([new RangeScan(180, 182, 95, 32, AnalysisMode.Credits)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_CardBelowTheBlackMinimumInARejectedGap_StaysACard()
    {
        // A blank black gap between acts holds a grey card at 38, below the black minimum, among its
        // last pages. Grey cards follow from 42. The lettering gate rejects the gap. A rejected range
        // makes only its black pages content, so the grey card stays a card and starts the card run.
        // Without it the run would be 12 s, under the minimum.
        var ffmpeg = KeyframeScan(Keyframes(0, 70, 2, (38, 38, 0, t => KeyframeVisuals.Card(t)), (20, 40, 100, KeyframeVisuals.BlankBlack), (42, 54, 0, t => KeyframeVisuals.Card(t))));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.KeyframeVisuals, 138.0, 154.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_SceneUnderTheMinimumAfterProbing_LeavesTheCardFallback()
    {
        // The roll's first pages hold more lettering and are 92 percent black, and the pages after
        // them are 100. The scene starts at the transition row at 20 and runs 14 s. The analyzer
        // admits it because the 2 s gap before it could bring it to the minimum. The boundary probe
        // finds no black there, so the scene fails the final duration check. With no candidate,
        // accepted is empty and the card finder falls back to the visuals. Every roll page is then a
        // card, including those before the transition row.
        var ffmpeg = KeyframeScan(Keyframes(0, 60, 2, (10, 18, 92, KeyframeVisuals.Black), (20, 34, 100, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.KeyframeVisuals, 110.0, 134.0)], candidates);
        Assert.Equal([new RangeScan(118, 120, 95, 32, AnalysisMode.Credits)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_SceneTrimmedUnderTheMinimumBesideARoll_KeepsItsBlackCards()
    {
        // A dark grey lead-in and 10 s of roll come first, then grey cards, then a separate 30 s
        // roll. The trim leaves the first scene under the minimum. It stays accepted because the
        // later roll meets the minimum, so its pages are black cards. They extend the grey cards, 10 s
        // on their own, into a card run from 26 to 48. The lead-in pages are content.
        var ffmpeg = KeyframeScan(Keyframes(
            0,
            120,
            2,
            (20, 24, 95, KeyframeVisuals.DarkGrey),
            (26, 36, 100, KeyframeVisuals.Black),
            (38, 48, 0, t => KeyframeVisuals.Card(t)),
            (80, 110, 100, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.BlackFrame, 180.0, 210.0), (SegmentSource.KeyframeVisuals, 126.0, 148.0)], candidates);
        Assert.Equal([new LumaDecode(124, 126.25, 160), new RangeScan(178, 180, 95, 32, AnalysisMode.Credits)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_LeadInProbeWalksBackOverAPageNotCountedBlack_ItIsABlackCard()
    {
        // A dim shot cuts to the roll just after 22. The roll's pages have their background at luma
        // 31, which the blackframe filter counts black at threshold 32. The page at 24 has its
        // background two levels higher, at 33, so the filter does not count it black, and it is
        // card-like. The lead-in probe matches pixels within two levels, so it walks back from the
        // level page at 26 over that page to the frame after the cut. The page lies inside the
        // refined scene and is a black card rather than a card before the roll, so there is no card
        // run.
        var ffmpeg = KeyframeScan(
            Keyframes(
                0,
                70,
                2,
                (20, 22, 88, KeyframeVisuals.Dark),
                (24, 24, 0, t => new KeyframeVisual(t, 33, 33, 33, 235, 0, 0)),
                (26, 56, 92, t => new KeyframeVisual(t, 31, 31, 31, 235, 0, 0))),
            lumaWindows: (_, window, _) => LumaWindows.Window(
                window.Start,
                (1 / LumaWindows.Fps, () => LumaWindows.Blob(21)),
                (24 - window.Start - (1 / LumaWindows.Fps), () => LumaWindows.Blob(31)),
                (2, () => LumaWindows.Blob(33)),
                (window.End - 26, () => LumaWindows.Blob(31))));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        Assert.Equal([(SegmentSource.BlackFrame, 22 + (1 / 24.0), 56.0)], candidates);
        Assert.Equal([new LumaDecode(22, 26.25, 160)], ffmpeg.Calls);
    }

    // ── Card credits from keyframe visuals ───────────────────────────────

    [Fact]
    public void TestParseKeyframeVisuals_ParsesLumaPercentilesAndSaturation()
    {
        // The other signalstats lines (YAVG, the U/V and hue stats) are noise to this parser.
        const string raw = """
            [Parsed_metadata_2 @ 0x0] frame:0    pts:0       pts_time:0
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=60
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YAVG=130.5
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YHIGH=200
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.UMIN=90
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATLOW=0
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=108.199
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.HUEAVG=180
            [Parsed_metadata_2 @ 0x0] frame:1    pts:20480   pts_time:2
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YAVG=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YHIGH=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATLOW=0
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=0
            """;

        var visuals = FFmpegOutputParser.ParseKeyframeVisuals(raw);

        Assert.Equal(2, visuals.Length);
        Assert.Equal(new KeyframeVisual(0.0, 16, 60, 200, 235, 0, 108.199), visuals[0]);
        Assert.Equal(new KeyframeVisual(2.0, 16, 16, 16, 235, 0, 0), visuals[1]);
    }

    [Fact]
    public void TestParseKeyframeVisuals_SkipsBlocksMissingAStat()
    {
        // A block missing a stat, whether mid-output or truncated at the end, must be dropped rather
        // than emitted with zeros: a zero saturation passes the credit-card gate and a zero spread
        // would fabricate a card.
        const string raw = """
            [Parsed_metadata_2 @ 0x0] frame:0 pts:0 pts_time:5
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=128
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YHIGH=128
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATLOW=0
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=12.5
            [Parsed_metadata_2 @ 0x0] frame:1 pts:1 pts_time:7
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=128
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATLOW=0
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=12.5
            [Parsed_metadata_2 @ 0x0] frame:2 pts:2 pts_time:9
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=128
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YHIGH=128
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
            """;

        var visual = Assert.Single(FFmpegOutputParser.ParseKeyframeVisuals(raw));

        Assert.Equal(new KeyframeVisual(5.0, 16, 128, 128, 235, 0, 12.5), visual);
    }

    [Fact]
    public void TestParseKeyframeVisuals_ParsesExponentNotation()
    {
        // Defensive: signalstats prints integers, but if a build ever emits exponent form the whole
        // numeric token must be parsed, not truncated at the mantissa, which would feed corrupt
        // values into detection and the cache.
        const string raw = """
            [Parsed_metadata_2 @ 0x0] frame:0 pts:0 pts_time:1e-05
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=1.6e+01
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=1.28e+02
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YHIGH=1.28e+02
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=2.35e+02
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATLOW=0
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=3.2e+01
            [Parsed_metadata_2 @ 0x0] frame:1 pts:1 pts_time:2
            """;

        var visual = Assert.Single(FFmpegOutputParser.ParseKeyframeVisuals(raw));

        Assert.Equal(new KeyframeVisual(1e-05, 16, 128, 128, 235, 0, 32), visual);
    }

    // Each row: a keyframe visual and whether it is a credit card.
    public static TheoryData<KeyframeVisual, bool> CreditCardKeyframeCases => new()
    {
        { KeyframeVisuals.Card(0), true },
        { KeyframeVisuals.WhiteCard(0), true },

        // Spread 9: the background is not dominant.
        { new KeyframeVisual(0, 16, 128, 137, 235, 0, 30), false },

        // Spread 8 with contrast 60 on both sides: both boundaries inclusive.
        { new KeyframeVisual(0, 68, 128, 136, 196, 0, 30), true },

        // Contrast 59: a bare wall or a fade.
        { new KeyframeVisual(0, 69, 128, 128, 187, 0, 30), false },

        { KeyframeVisuals.BlankBlack(0), false },
        { KeyframeVisuals.Dark(0), false },
        { KeyframeVisuals.FlatWithSubject(0), false },

        // Card at the exclusive saturation maximum.
        { KeyframeVisuals.Card(0, saturation: 96), false },
    };

    [Theory]
    [MemberData(nameof(CreditCardKeyframeCases))]
    public void TestIsCreditCardKeyframe(KeyframeVisual visual, bool expected)
    {
        Assert.Equal(expected, CardRunFinder.IsCreditCardKeyframe(visual));
    }

    // Each row: keyframes none of which is black, minimum credit duration, expected (Start, End) or null.
    public static TheoryData<(BlackFrame[] Rows, KeyframeVisual[] Visuals), int, (double Start, double End)?> CardCreditsCases => new()
    {
        // Over-extension: dense credits 0-20, periodic isolated tail cards every 8s -> trim to 20.
        { Scan(Seq(58, 2, (0, 20), (30, 30), (38, 38), (46, 46), (54, 54))), 15, (0, 20) },

        // Leading over-extension: an isolated pre-credit card bridges into a dense block (4s GOP)
        // -> start anchored to the dense block, not the stray pre-card.
        { Scan(Seq(88, 4, (36, 36), (52, 80))), 15, (52, 80) },

        // Clean dense card run -> unchanged.
        { Scan(Seq(54, 2, (30, 54))), 15, (30, 54) },

        // Mid-body ident interlude (6s of non-card bracketed by dense cards) -> preserved.
        { Scan(Seq(60, 2, (0, 28), (36, 60))), 15, (0, 60) },

        // Interlude near the end (cards resume densely after) -> preserved.
        { Scan(Seq(60, 2, (0, 48), (56, 60))), 15, (0, 60) },

        // Sparse all-card credits (8s GOP) -> kept (100% density, trailing gap within scaled trim).
        { Scan(Seq(40, 8, (0, 40))), 15, (0, 40) },

        // Uniform sparse long-GOP credits (12s cadence) -> kept; the trim keys off the run's own
        // cadence, so an all-card run is never discarded even when its gap exceeds the capped bridge.
        { Scan(Seq(48, 12, (0, 48))), 15, (0, 48) },

        // All-card run whose 21s cadence exceeds the fixed bridge, with nothing non-card between
        // -> stays one run instead of splitting into one-frame runs that each miss the minimum.
        { Scan(Seq(63, 21, (0, 63))), 60, (0, 63) },

        // Two real runs separated by a long gap -> latest selected.
        { Scan(Seq(80, 2, (0, 20), (60, 80))), 15, (60, 80) },

        // An earlier dense run (0-20) must not capture or extend into a later long-GOP credit run
        // (60-96, 12s cadence): real content separates the groups, so the latest run is returned.
        { Scan(NotBlack([.. Cards(0, 20, 2), .. Busy(22, 58, 2), .. Cards(60, 96, 12)])), 15, (60, 96) },

        // Dense non-card content, then static cards on a 12s-keyframe source: grouping must key off the
        // card cadence, not the content cadence, or every 12s card gap splits the run.
        { Scan(NotBlack([.. Busy(0, 58, 2), .. Cards(60, 96, 12)])), 15, (60, 96) },

        // A substantial dense body followed by isolated cards every 8s out to the window edge -> the
        // trim anchors to the dense-body cadence and cuts the sparse tail back to the real block.
        { Scan(NotBlack([.. Cards(0, 20, 2), .. Cards(28, 196, 8)])), 15, (0, 20) },

        // Sparse isolated cards bridged across busy 2s content (brief dense head, then a lone card
        // every 8s) -> rejected by the card-density floor: most keyframes in the span are busy
        // content, so this reads as normal content with occasional static shots, not a card sequence.
        { Scan(Seq(54, 2, (0, 6), (14, 14), (22, 22), (30, 30), (38, 38), (46, 46), (54, 54))), 15, null },

        // Two card-like keyframes 18s apart with busy keyframes between them -> not credits.
        { Scan(Seq(18, 2, (0, 0), (18, 18))), 15, null },

        // Final card spaced just within cadence (4s) -> kept, not over-trimmed.
        { Scan(Seq(44, 2, (0, 40), (44, 44))), 15, (0, 44) },

        // Only 10s of card -> below the minimum duration.
        { Scan(Seq(40, 2, (30, 40))), 15, null },

        // Dark (low luma) but detailed content spreads wide within the dark range, like a night scene -> not a card.
        { Scan(Times(0, 58, 2).Select(t => new Keyframe(t, 50, KeyframeVisuals.Dark(t)))), 15, null },

        // Uniform but vividly saturated frames are excluded on purpose (see CardRunFinder).
        { Scan(NotBlack(CreateCardCreditVisuals(cardStart: 0, cardEnd: 20, cardSaturation: 200))), 15, null },

        // All busy content -> null.
        { Scan(Seq(60, 2)), 15, null },
    };

    [Theory]
    [MemberData(nameof(CardCreditsCases))]
    public void TestCardRunFinder_FindCreditRange((BlackFrame[] Rows, KeyframeVisual[] Visuals) scan, int minimumDuration, (double Start, double End)? expected)
    {
        var (rows, visuals) = scan;

        var range = CardRunFinder.FindCreditRange(visuals, rows, blackMinimum: 85, minimumDuration, blackFrameScenes: []);

        if (expected is null)
        {
            Assert.Null(range);
            return;
        }

        Assert.NotNull(range);
        Assert.Equal(expected.Value.Start, range.Start);
        Assert.Equal(expected.Value.End, range.End);
    }

    // Each row: the keyframe scan, one black row and one visual per keyframe, the black scenes the
    // black-frame rules accepted, empty when they found no credits, expected (Start, End) or null.
    public static TheoryData<(BlackFrame[] Rows, KeyframeVisual[] Visuals), (double Start, double End)[], (double Start, double End)?> BlackKeyframeCases
    {
        get
        {
            var data = new TheoryData<(BlackFrame[] Rows, KeyframeVisual[] Visuals), (double Start, double End)[], (double Start, double End)?>();

            // Card has an optional saturation, so a span takes it as a lambda.
            Func<double, KeyframeVisual?> card = t => KeyframeVisuals.Card(t);

            // Scattered flat shots in an epilogue before a roll (CITY THE ANIMATION E09), the measured
            // false positive. Under the percentile rule a flat background with a subject in front is
            // not a card at all, so the roll has no card density and stays the black-frame candidate's.
            var epilogue = Keyframes(
                0,
                118,
                2,
                (22, 24, 0, KeyframeVisuals.FlatWithSubject),
                (34, 36, 0, KeyframeVisuals.FlatWithSubject),
                (54, 56, 0, KeyframeVisuals.FlatWithSubject),
                (62, 64, 0, KeyframeVisuals.FlatWithSubject),
                (68, 118, 100, KeyframeVisuals.Black));
            data.Add(Scan(epilogue), [(68, 118)], null);

            // White cards then a roll: the black cards extend the run. The roll's black rows sit 0.4 ms
            // after their visuals, as the two ffmpeg filters can print them.
            var cardsThenRoll = Keyframes(0, 80, 2, (0, 40, 0, card), (42, 80, 100, KeyframeVisuals.Black));
            data.Add(Scan(cardsThenRoll.Select(keyframe => keyframe.Percentage == 100 ? keyframe with { Time = keyframe.Time + 0.0004 } : keyframe)), [(42, 80)], (0, 80));

            // White, black, white: one run.
            data.Add(Scan(Keyframes(0, 80, 2, (0, 20, 0, card), (22, 60, 100, KeyframeVisuals.Black), (62, 80, 0, card))), [(22, 60)], (0, 80));

            // A blank black page in the middle of a roll inside an accepted scene: black is black there,
            // so the page is not content and the run covers the roll and the cards after it.
            data.Add(Scan(Keyframes(0, 40, 2, (10, 10, 100, KeyframeVisuals.BlankBlack), (0, 18, 100, KeyframeVisuals.Black), (20, 40, 0, card))), [(0, 18)], (0, 40));

            // A grey vanity card in the middle of a roll, inside the accepted scene that merged across
            // it: a black card like the roll pages around it, so the roll still has no card density.
            data.Add(Scan(Keyframes(0, 60, 2, (28, 32, 0, card), (0, 60, 100, KeyframeVisuals.Black))), [(0, 60)], null);

            // Roll only: as an accepted black scene there is no card density and the roll is the
            // black-frame candidate's; with no candidate the visuals alone decide and it is recovered here.
            var rollOnly = Scan(Keyframes(0, 60, 2, (0, 60, 100, KeyframeVisuals.Black)));
            data.Add(rollOnly, [(0, 60)], null);
            data.Add(rollOnly, [], (0, 60));

            // Solid white frames on fully black rows inside an accepted scene. No scan pairs the two;
            // the contradiction is deliberate. A white screen is content even when the black-frame
            // evidence misclassifies it, so it must not extend an accepted black scene.
            data.Add(Scan(Keyframes(0, 60, 2, (0, 60, 100, KeyframeVisuals.WhiteScreen))), [(0, 60)], null);

            // Short white cards then a short roll, each below the minimum on its own, qualify together.
            data.Add(Scan(Keyframes(0, 24, 2, (0, 10, 0, card), (12, 24, 100, KeyframeVisuals.Black))), [], (0, 24));

            // Sparse black cards on a 21 s cadence that the black-frame rules could not confirm:
            // recovered as the old fallback did.
            data.Add(Scan(Keyframes(0, 63, 21, (0, 63, 100, KeyframeVisuals.Black))), [], (0, 63));

            // A dark grey lead-in at 90 percent black before a full-black roll. Its pages are black and
            // pass the card test, but the accepted black scene starts at the roll, so the lead-in is
            // content here too and the run starts at the roll.
            data.Add(Scan(Keyframes(0, 100, 2, (0, 40, 90, KeyframeVisuals.DarkGrey), (42, 78, 100, KeyframeVisuals.Black), (80, 100, 0, card))), [(42, 78)], (42, 100));

            // Sparse white cards on a 12 s cadence then a dense roll: the trim cadence comes from the
            // white cards, so the roll cannot trim them away.
            data.Add(Scan([.. Keyframes(0, 24, 12, (0, 24, 0, card)), .. Keyframes(36, 80, 2, (36, 80, 100, KeyframeVisuals.Black))]), [(36, 80)], (0, 80));

            // Dark detailed keyframes at 89 percent are black at the minimum of 85, but their luma spreads
            // too wide for a card. With no accepted scene the visuals alone decide, so they are content
            // and stay in the density ratio: 13 cards among 31 keyframes fail the floor.
            data.Add(Scan(Times(0, 60, 2).Select(t => t % 10 is 0 or 4 ? new Keyframe(t, 0, KeyframeVisuals.Card(t)) : new Keyframe(t, 89, KeyframeVisuals.Dark(t)))), [], null);

            // One-keyframe black flashes before an interval-confirmed roll: the accepted black scene
            // starts at the roll, so the flashes are content and the run starts there too.
            var blackFlashes = Keyframes(
                0,
                80,
                2,
                (0, 0, 100, KeyframeVisuals.Black),
                (10, 10, 100, KeyframeVisuals.Black),
                (20, 20, 100, KeyframeVisuals.Black),
                (30, 50, 100, KeyframeVisuals.Black),
                (52, 80, 0, card));
            data.Add(Scan(blackFlashes), [(30, 50)], (30, 80));

            // Black card, white card, busy frame repeating, with no black-frame candidate: the visuals
            // alone decide, as the old fallback did, and two cards in three keep the run.
            data.Add(Scan(Times(0, 60, 2).Select(t => t % 6 == 0 ? new Keyframe(t, 100, KeyframeVisuals.Black(t)) : new Keyframe(t, 0, t % 6 == 2 ? KeyframeVisuals.Card(t) : KeyframeVisuals.Content(t)))), [], (0, 60));

            // Saturated black keyframes flat enough to pass the card test: content, not cards, with
            // no scene to fall back on.
            data.Add(Scan(Keyframes(0, 60, 2, (0, 60, 100, KeyframeVisuals.TintedFlat))), [], null);

            // Red lettering on black raises the mean saturation, not the background's: a card run
            // through the no-scene fallback like white lettering would be.
            data.Add(Scan(Keyframes(0, 60, 2, (0, 60, 100, KeyframeVisuals.RedText))), [], (0, 60));

            // White cards then a short roll, content, then a separate longer roll: the black-frame
            // analyzer accepts both scenes and returns each as a candidate. The short roll's pages are
            // black cards, so the white cards and the short roll make one run that qualifies on the
            // white cards' density. The longer roll has no card density and stays that analyzer's.
            data.Add(Scan(Keyframes(0, 140, 2, (0, 10, 0, card), (12, 30, 100, KeyframeVisuals.Black), (100, 140, 100, KeyframeVisuals.Black))), [(12, 30), (100, 140)], (0, 30));

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BlackKeyframeCases))]
    public void TestCardRunFinder_BlackKeyframes((BlackFrame[] Rows, KeyframeVisual[] Visuals) scan, (double Start, double End)[] blackFrameScenes, (double Start, double End)? expected)
    {
        var (rows, visuals) = scan;

        var range = CardRunFinder.FindCreditRange(
            visuals,
            rows,
            blackMinimum: 85,
            minimumDuration: 15,
            [.. blackFrameScenes.Select(scene => new TimeRange(scene.Start, scene.End))]);

        if (expected is null)
        {
            Assert.Null(range);
            return;
        }

        Assert.NotNull(range);
        Assert.Equal(expected.Value.Start, range.Start);
        Assert.Equal(expected.Value.End, range.End);
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

        // The real credits are the last scene.
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

        // Second block (post-stinger credits).
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

    /// <summary>
    /// Every candidate the keyframe analyzer returns with card detection on, by source, so a stray
    /// card run fails a test as a wrong black-frame candidate does.
    /// </summary>
    private static async Task<List<(SegmentSource Source, double Start, double End)>> KeyframeCandidates(KeyframeAnalyzer analyzer, QueuedEpisode episode)
        => [.. (await analyzer.DetectCreditsAsync(episode, 85, 32, 15, detectCardCredits: true)).Select(c => (c.Source, c.Segment.Start, c.Segment.End))];

    private static async Task<List<Segment>> BlackFrameCredits(KeyframeAnalyzer analyzer, QueuedEpisode episode)
        => [.. (await analyzer.DetectCreditsAsync(episode, 85, 32, 15, detectCardCredits: false)).Where(c => c.Source == SegmentSource.BlackFrame).Select(c => c.Segment)];

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

    private static KeyframeAnalyzer CreateKeyframeAnalyzer(IFFmpegService ffmpegService, PluginConfiguration? configuration = null, DetectionCacheService? cacheService = null)
    {
        return new(NullLogger<KeyframeAnalyzer>.Instance, ffmpegService, cacheService ?? DatabaseTestHelpers.CreateTempCacheService(), configuration ?? new PluginConfiguration());
    }

    /// <summary>
    /// Creates a stub whose keyframe scan reports <paramref name="keyframes"/>, whose range probes
    /// return <paramref name="probeFrames"/>, whose blackdetect scans return
    /// <paramref name="intervals"/> and whose lead-in decodes return what
    /// <paramref name="lumaWindows"/> returns, or fail.
    /// </summary>
    private static StubFFmpegService KeyframeScan(
        IEnumerable<Keyframe> keyframes,
        BlackFrame[]? probeFrames = null,
        BlackInterval[]? intervals = null,
        Func<QueuedEpisode, TimeRange, int, LumaWindow?>? lumaWindows = null)
    {
        var (rows, visuals) = Scan(keyframes);
        return new()
        {
            CreditsBlackFrames = (_, _) => rows,
            KeyframeVisuals = _ => visuals,
            RangeBlackFrames = (_, _, _, _, _) => probeFrames ?? [],
            BlackIntervals = (_, _, _, _) => intervals ?? [],
            LumaWindows = lumaWindows ?? ((_, _, _) => null),
        };
    }

    /// <summary>
    /// Creates a stub whose keyframe credits scan returns <paramref name="creditsFrames"/>, whose
    /// range probes return <paramref name="probeFrames"/>, and whose blackdetect scans return
    /// <paramref name="intervals"/>.
    /// </summary>
    private static StubFFmpegService CreditsScan(
        BlackFrame[] creditsFrames,
        BlackFrame[]? probeFrames = null,
        BlackInterval[]? intervals = null,
        KeyframeVisual[]? visuals = null,
        Func<QueuedEpisode, TimeRange, int, LumaWindow?>? lumaWindows = null) => new()
        {
            CreditsBlackFrames = (_, _) => creditsFrames,
            RangeBlackFrames = (_, _, _, _, _) => probeFrames ?? [],
            BlackIntervals = (_, _, _, _) => intervals ?? [],
            KeyframeVisuals = _ => visuals ?? [],
            LumaWindows = lumaWindows ?? ((_, _, _) => null),
        };

    /// <summary>
    /// Keyframes ten seconds apart from 0 to 120: content, then a roll that is black from the page at
    /// <paramref name="pageTime"/> to 80, then grey cards. The roll is sparse, so the analyzer probes
    /// it with blackdetect, and the interval from 40 to 80 makes it a scene that starts on
    /// blackdetect's clock. <paramref name="page"/> gives the page a level visual, a dim one or none;
    /// "level before dim" is a level page before a dim start frame at 50; "content" makes the page a
    /// content keyframe, so the roll's first black page is the start frame at 50. The lead-in decode
    /// fails, so a trimmed scene starts at its level start frame.
    /// </summary>
    private static StubFFmpegService PageBeforeTheRoll(string page, double pageTime)
    {
        var pageVisual = page switch
        {
            "no visual" => null,
            "dim" => KeyframeVisuals.DarkGrey(pageTime),
            "content" => KeyframeVisuals.Content(pageTime),
            _ => KeyframeVisuals.Black(pageTime),
        };
        var rollStart = page == "content" ? 50 : pageTime;
        double[] times = [0, 10, 20, 30, pageTime, 50, 60, 70, 80, 90, 100, 110, 120];
        return KeyframeScan(
            times.Select(time => new Keyframe(time, time >= rollStart && time <= 80 ? 100 : 0, time == pageTime ? pageVisual : time switch
            {
                < 40 => KeyframeVisuals.Content(time),
                50 when page == "level before dim" => KeyframeVisuals.DarkGrey(time),
                <= 80 => KeyframeVisuals.Black(time),
                _ => KeyframeVisuals.Card(time),
            })),
            intervals: [new BlackInterval(40, 80)]);
    }

    private static KeyframeVisual[] CreateCardCreditVisuals(double cardStart, double cardEnd, double cardSaturation)
    {
        var visuals = new List<KeyframeVisual>();
        for (var time = 0.0; time < cardStart; time += 2)
        {
            visuals.Add(KeyframeVisuals.Content(time));
        }

        for (var time = cardStart; time <= cardEnd; time += 2)
        {
            visuals.Add(KeyframeVisuals.Card(time, cardSaturation));
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
        => Times(from, to, step).Select(t => KeyframeVisuals.Card(t));

    private static IEnumerable<KeyframeVisual> Busy(double from, double to, double step)
        => Times(from, to, step).Select(t => KeyframeVisuals.Content(t));

    private static IEnumerable<KeyframeVisual> Black(double from, double to, double step)
        => Times(from, to, step).Select(t => KeyframeVisuals.Black(t));

    /// <summary>
    /// Keyframes every <paramref name="step"/> seconds from 0 to <paramref name="end"/>, none of them
    /// black; those inside any of the <paramref name="cards"/> spans (inclusive) are credit cards, the
    /// rest busy content.
    /// </summary>
    private static Keyframe[] Seq(double end, double step, params (double From, double To)[] cards)
        => NotBlack(Times(0, end, step).Select(t => cards.Any(c => t >= c.From - 1e-9 && t <= c.To + 1e-9)
            ? KeyframeVisuals.Card(t)
            : KeyframeVisuals.Content(t)));

    /// <summary>
    /// Keyframes for <paramref name="visuals"/> that the blackframe filter does not count as black
    /// at all, as cards and busy content are not.
    /// </summary>
    private static Keyframe[] NotBlack(IEnumerable<KeyframeVisual> visuals)
        => [.. visuals.Select(visual => new Keyframe(visual.Time, 0, visual))];

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
