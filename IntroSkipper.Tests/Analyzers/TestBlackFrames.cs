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
    /// A 10-bit gray source at luma 80 (20 on the 8-bit scale) is black at threshold 28 when read
    /// as gray, but not after conversion to limited-range yuv420p (luma 33). FFmpeg emits two or
    /// three frames for this synthetic clip.
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

            Assert.InRange(frames.Length, 2, 3);
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

    /// <summary>
    /// VP9 ignores <c>-skip_frame nokey</c>, so the listing must select packet keyframes. The
    /// default VP9 stream is smaller than the non-default FFV1 stream before it, so a codec probe
    /// that picks the largest stream reads FFV1 and leaves the select out. ffmpeg decodes the
    /// VP9 stream, so the probe must name VP9.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task DetectKeyFramesAsync_ListsOnlyKeyframesOfVp9Sources()
    {
        var path = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-vp9.mkv");
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            [
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=s=64x64:r=25:d=6",
                "-f", "lavfi", "-i", "testsrc2=s=32x32:r=25:d=6",
                "-map", "0", "-map", "1",
                "-c:v:0", "ffv1", "-disposition:v:0", "0",
                "-c:v:1", "libvpx-vp9", "-g:v:1", "50", "-keyint_min:v:1", "50", "-deadline:v:1", "realtime", "-disposition:v:1", "default",
                path,
            ]);
        try
        {
            var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "vp9", Path = path, Duration = 6 };

            var actual = await FfmpegTestHelpers.CreateFFmpegService().DetectKeyFramesAsync(episode, new(0, 6), AnalysisMode.Introduction);

            Assert.Equal([0, 2, 4], actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The stderr of a zero-frame run on an H.264 file whose title tag repeats a mapping block
    /// for a VP9 stream, as jellyfin-ffmpeg 8.1.2 prints it. Lines end in CRLF on Windows.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ParseMappedVideoCodec_ReadsTheMappingBlockNotATagThatRepeatsIt(string newLine)
    {
        var stderr = """
            Input #0, matroska,webm, from 'episode.mkv':
              Metadata:
                title           : x
                                : Stream mapping:
                                :   Stream #0:1 -> #0:0 (vp9 (native) -> wrapped_avframe (native))
              Duration: 00:00:02.00, start: 0.000000, bitrate: 60 kb/s
              Stream #0:0: Video: h264 (High), yuv420p(tv, progressive), 64x64 [SAR 1:1 DAR 1:1], 25 fps, 25 tbr, 1k tbn
            Stream mapping:
              Stream #0:0 -> #0:0 (h264 (native) -> wrapped_avframe (native))
            Press [q] to stop, [?] for help
            Output #0, null, to 'pipe:':
              Metadata:
                title           : x
                                : Stream mapping:
                                :   Stream #0:1 -> #0:0 (vp9 (native) -> wrapped_avframe (native))
            """.ReplaceLineEndings(newLine);

        Assert.Equal("h264", FFmpegOutputParser.ParseMappedVideoCodec(stderr));
    }

    [Fact]
    public void ParseMappedVideoCodec_ReturnsNullWithoutAMappingBlock()
    {
        // A file without a video stream: ffmpeg maps nothing and prints no block.
        const string Stderr = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'audio.m4a':
              Stream #0:0[0x1](und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, mono, fltp, 69 kb/s (default)
            Output #0, null, to 'pipe:':
            [out#0/null @ 0000000000000000] Output file does not contain any stream
            Error opening output file -.
            """;

        Assert.Null(FFmpegOutputParser.ParseMappedVideoCodec(Stderr));
    }

    [FactSkipFFmpegTests]
    public async Task TestDetectKeyFrames_ClipsScanToSearchRange()
    {
        // credits.mp4 has a keyframe every 10 s. ffmpeg also lists the one at 110 before -to
        // trims it, and a snap must never land there.
        var actual = await FfmpegTestHelpers.CreateFFmpegService().DetectKeyFramesAsync(
            FfmpegTestHelpers.QueueFile("video/credits.mp4"),
            new(95, 102),
            AnalysisMode.Introduction);

        Assert.Equal([100], actual);
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
        var (result, calls) = await RunChapterAnalysisAsync(
            blackRanges: [new TimeRange(2274, 2276), new TimeRange(2399, 2420)],
            chapterStarts: [2417.76],
            duration: 2444.6);

        // The search probes a second from the chapter, then steps back a second at a time. Every
        // probe is black, so it runs out 16 s before the chapter without finding where the run starts.
        Assert.Null(result);
        Assert.Equal(
        [
            Probe(2417.76, 2418.76),
            Probe(2416.76, 2417.76),
            Probe(2415.76, 2416.76),
            Probe(2414.76, 2415.76),
            Probe(2413.76, 2414.76),
            Probe(2412.76, 2413.76),
            Probe(2411.76, 2412.76),
            Probe(2410.76, 2411.76),
            Probe(2409.76, 2410.76),
            Probe(2408.76, 2409.76),
            Probe(2407.76, 2408.76),
            Probe(2406.76, 2407.76),
            Probe(2405.76, 2406.76),
            Probe(2404.76, 2405.76),
            Probe(2403.76, 2404.76),
            Probe(2402.76, 2403.76),
            Probe(2401.76, 2402.76),
        ], calls);

        static RangeScan Probe(double start, double end) => new(start, end, 85, 28, AnalysisMode.Credits);
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

        var (result, calls) = await RunChapterAnalysisAsync(
            blackRanges: [new TimeRange(ActBreakChapterStart, ActBreakBlackRunEnd), new TimeRange(PreCreditsFadeStart, CreditsBlackRunEnd)],
            chapterStarts: [ActBreakChapterStart, CreditsChapterStart],
            duration: EpisodeDuration);

        Assert.NotNull(result);
        Assert.Equal(CreditsChapterStart, result.Start, 3);
        Assert.Equal(EpisodeDuration, result.End, 3);

        // A second from the credits chapter, then a second at a time back to the first that is not
        // black, 2393; the act-break chapter is never probed.
        Assert.Equal(
        [
            Probe(2400, 2401),
            Probe(2399, 2400),
            Probe(2398, 2399),
            Probe(2397, 2398),
            Probe(2396, 2397),
            Probe(2395, 2396),
            Probe(2394, 2395),
            Probe(2393, 2394),
        ], calls);

        static RangeScan Probe(double start, double end) => new(start, end, 85, 28, AnalysisMode.Credits);
    }

    [Fact]
    public async Task TryAnalyzeChaptersAsync_ReturnsNullWhenChapterCreditsExceedMaximumDuration()
    {
        // The chapter exceeds the default max credits duration (450s), so chapter analysis must fall back.
        var (result, calls) = await RunChapterAnalysisAsync(
            blackRanges: [new TimeRange(2400, 2410)],
            chapterStarts: [2400],
            duration: 2900);

        Assert.Null(result);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task TestMergeScenesAcross20SecondGap()
    {
        // Two dense black parts whose black keyframes lie exactly 20 s apart, 9.5 to 29.5, are one
        // run. The gap holds 10 widely spaced frames so the run's density (40/50 = 80%) stays well
        // above the 50% threshold, isolating the 20 s boundary.
        var frames = new List<BlackFrame>();
        frames.AddRange(CreateDenseFrames(startTime: 0, endTime: 9.5, percentage: 95));
        for (var i = 0; i < 10; i++)
        {
            frames.Add(new BlackFrame(10, 10.0 + (i * 2.0), 20 + i));
        }

        frames.AddRange(CreateDenseFrames(startTime: 29.5, endTime: 39.0, percentage: 95, startFrame: 30));

        // minimum=85, sceneChange=96 (with floor=0 these are direct values)
        var scenes = await ScanOf(frames, blackMinimum: 85, sceneChange: 96).ScenesAsync(15, ProbeFinding());

        Assert.Single(scenes);

        // StartTime == 0.0 also validates the transition-frame search: no frame reaches
        // the sceneChange threshold (96), so the start is not shifted forward.
        Assert.Equal(0.0, scenes[0].StartTime);
        Assert.Equal(39.0, scenes[0].EndTime);
    }

    [Fact]
    public async Task TestDoesNotMergeScenesAcross20Point5SecondGap()
    {
        // Two dense black parts whose black keyframes lie 20.5 s apart, 9.5 to 30, are two runs.
        List<BlackFrame> frames =
        [
            .. CreateDenseFrames(startTime: 0, endTime: 9.5, percentage: 95),
            .. CreateDenseFrames(startTime: 10, endTime: 29.5, percentage: 10),
            .. CreateDenseFrames(startTime: 30, endTime: 39.5, percentage: 95),
        ];

        var scenes = await ScanOf(frames, blackMinimum: 85, sceneChange: 96).ScenesAsync(5, ProbeFinding());

        Assert.Equal(2, scenes.Count);
    }

    [Fact]
    public async Task TestRunAcrossSparseGap_IsGatedOnDensityAsAWhole()
    {
        // Two dense black parts whose black keyframes lie 19.5 s apart, 7.5 to 27, are one run
        // however few of the frames between them are black. The run is 32 black frames of 70, under
        // half, so it fails the density gate and neither dense part survives on its own.
        List<BlackFrame> frames =
        [
            .. CreateDenseFrames(startTime: 0, endTime: 7.5, percentage: 95),
            .. CreateDenseFrames(startTime: 8, endTime: 26.5, percentage: 10),
            .. CreateDenseFrames(startTime: 27, endTime: 34.5, percentage: 95),
        ];

        var scenes = await ScanOf(frames, blackMinimum: 85, sceneChange: 96).ScenesAsync(5, ProbeFinding());

        Assert.Empty(scenes);
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

    [Theory]
    [InlineData("black", 120.0)]
    [InlineData("tinted", null)]
    [InlineData("saturated background", 120.0)]
    [InlineData("blank", null)]
    [InlineData("one page", null)]
    [InlineData("most pages", 120.0)]
    [InlineData("half the pages", null)]
    [InlineData("none", 120.0)]
    [InlineData("dense text", null)]
    [InlineData("red text", 120.0)]
    [InlineData("dense red text", 120.0)]
    [InlineData("dark highlight", null)]
    [InlineData("cut then highlight", null)]
    [InlineData("dark grey lead-in", 130.0)]
    [InlineData("lifted blacks", 120.0)]
    [InlineData("mixed black levels", 134.0)]
    [InlineData("letterboxed dark lead-in", 130.0)]
    [InlineData("letterboxed dark majority", 138.0)]
    [InlineData("big text", 120.0)]
    [InlineData("dense first pages", 124.0)]
    [InlineData("dark picture after a black head", null)]
    [InlineData("dense white text on a tint", 120.0)]
    [InlineData("dense warm grey text", 120.0)]
    [InlineData("dense coloured text", 120.0)]
    [InlineData("tinted dark lead-in majority", 138.0)]
    [InlineData("an eighth dark picture", 120.0)]
    public async Task DetectCreditsAsync_GatesBlackScenesOnTheirVisuals(string visualsKind, double? expectedStart)
    {
        // A dense black roll at 20 to 54. Its visuals decide: text pages are a roll, whatever the
        // background's saturation; blank pages are a gap between acts, not credits, until lettering
        // shows on more than half of them; no visuals at all leave the black-frame result alone.
        // Coloured lettering is a roll like any other. A dark scene with one lit spot on every page,
        // tinted or not, is dim on every page, so it is lead-in to its end and not a roll; a cut
        // followed by such keyframes is lettered on half its pages and is not one either. A dark
        // grey lead-in, black to the blackframe filter with its darkest tenth above the roll's, is
        // not part of the roll; a roll with lifted blacks sets the scene's black level and is a
        // roll; a roll authored at two black levels starts at its darker part.
        // Behind letterbox bars a dark scene's darkest tenth is black like the roll's, and its 90th
        // percentile gives it away, however long it runs; large lettering lifts a roll page's 90th
        // percentile onto the text and is a roll. Pages dense enough to lift the 90th percentile
        // into the dark band read as dim content, so a roll that opens on them starts after them
        // and a roll of nothing else is rejected. No frames are decoded here, so every lead-in
        // takes the policy's keyframe start; the cut start's own tests cover the frame it moves
        // to. A page whose 90th percentile reaches the threshold of 28 is at most nine tenths
        // black, so dense and large lettering and the pages behind bars sit at 89. That is still
        // black here: a scan of the roll alone has no zero rows, so its floor stays at 30, its
        // black minimum at 89 and its scene-change threshold at 96, above the roll's 95, so the
        // lead-in trim and not the transition shift starts a roll after its dim pages. No fixture
        // gives a card run: an accepted roll's pages are black cards, the pages of a rejected roll
        // are content, and pages without a visual are invisible to the card finder.
        // A faintly tinted dark picture with dim highlights is rejected whole even when the page at
        // its head sits at the black level and so ends no lead-in; dense lettering after such a head
        // stays a roll when it is near white, grey with a warm cast under the tint band, or strongly
        // coloured. The majority is read after the lead-in, so a tinted lead-in longer than the roll
        // is trimmed, and a roll with an eighth of its pages a dark picture is still a roll.
        Keyframe[] keyframes = visualsKind switch
        {
            "black" => Roll((20, 54, 95, KeyframeVisuals.Black)),
            "tinted" => Roll((20, 54, 95, KeyframeVisuals.Tinted)),
            "saturated background" => Roll((20, 54, 95, t => new KeyframeVisual(t, 16, 16, 16, 235, 30, 0))),
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
            "dark picture after a black head" => Roll((20, 20, 95, t => new KeyframeVisual(t, 15, 17, 19, 179, 1.1, 0)), (20.5, 54, 95, t => new KeyframeVisual(t, 15, 17, 26, 100, 2, 0))),
            "dense white text on a tint" => Roll((20, 20, 95, KeyframeVisuals.Black), (20.5, 54, 95, t => new KeyframeVisual(t, 10, 16, 37, 241, 1.2, 0))),
            "dense warm grey text" => Roll((20, 20, 95, KeyframeVisuals.Black), (20.5, 54, 95, t => new KeyframeVisual(t, 16, 16, 30, 150, 0.9, 0))),
            "dense coloured text" => Roll((20, 20, 95, KeyframeVisuals.Black), (20.5, 54, 95, t => new KeyframeVisual(t, 16, 16, 60, 177, 12, 0))),
            "tinted dark lead-in majority" => Roll((20, 37.5, 95, t => new KeyframeVisual(t, 16, 16, 32, 150, 3, 0)), (38, 54, 95, KeyframeVisuals.Black)),
            "an eighth dark picture" => Roll((20, 54, 95, t => (t - 20) % 4 == 0.5 ? new KeyframeVisual(t, 16, 16, 26, 100, 2, 0) : KeyframeVisuals.Black(t))),
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
    public async Task DetectCreditsAsync_TrimmedScene_StartsOnTheCut()
    {
        // Keyframes two seconds apart, a dark grey lead-in to 28 and the roll from 30, whose first
        // page differs from the lead-in's last by 40. The cut decode reads the gap between the two
        // keyframes, and the frame at 129 carries the cut, so the scene starts there. The lead-in
        // pages lie outside the refined scene, so they are content to the card finder and the
        // black-frame candidate is the only one.
        var ffmpeg = KeyframeScan(CutAfterDarkGreyLeadIn(), cutWindows: (_, window) => CutFrames(window, cutFrame: 24));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 129.0, 154.0)], candidates);
        Assert.Equal([new CutDecode(128, 130)], ffmpeg.Calls);
    }

    [Theory]
    [InlineData(44, null)]
    [InlineData(46, 128.5)]
    public async Task DetectCreditsAsync_MinimumReadsTheSceneBeforeTheCutStart(double lastRollPage, double? expectedStart)
    {
        // The lead-in trims the scene to the roll from 30, and the cut frame at 128.5 lies 1.5 s
        // before it. A roll to 44 is 14 s, under the 15 s minimum, and the cut start would lift it to
        // 15.5 s; it only refines, so the scene stays rejected and its gap is not decoded. A roll to
        // 46 meets the minimum on its own and starts on the cut.
        var ffmpeg = KeyframeScan(
            Keyframes(20, 56, 2, (20, 28, 95, KeyframeVisuals.DarkGrey), (30, 30, 95, KeyframeVisuals.BlackAfterCut), (32, lastRollPage, 95, KeyframeVisuals.Black)),
            cutWindows: (_, window) => CutFrames(window, cutFrame: 12));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        (SegmentSource, double, double)[] expected = expectedStart is { } start ? [(SegmentSource.BlackFrame, start, lastRollPage + 100)] : [];
        Call[] decodes = expectedStart is null ? [] : [new CutDecode(128, 130)];
        Assert.Equal(expected, candidates);
        Assert.Equal(decodes, ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_CutStartDecodesEachProbedScene()
    {
        // Two black scenes with a dark grey lead-in each and content between them, too far apart to
        // merge. Each roll's first page follows a cut, so both gaps are decoded, and each scene
        // starts on its own gap's cut frame, a second after the lead-in's last page.
        var ffmpeg = KeyframeScan(
            Keyframes(20, 94, 2, (20, 20, 95, KeyframeVisuals.DarkGrey), (22, 22, 95, KeyframeVisuals.BlackAfterCut), (24, 40, 95, KeyframeVisuals.Black), (64, 64, 95, KeyframeVisuals.DarkGrey), (66, 66, 95, KeyframeVisuals.BlackAfterCut), (68, 94, 95, KeyframeVisuals.Black)),
            cutWindows: (_, window) => CutFrames(window, cutFrame: 24));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 121.0, 140.0), (SegmentSource.BlackFrame, 165.0, 194.0)], candidates);
        Assert.Equal([new CutDecode(120, 122), new CutDecode(164, 166)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_CutStartIsCached()
    {
        // The cut frame is a function of the file between the two keyframes, so a second analysis
        // of the episode reads it from the detection cache instead of decoding.
        var ffmpeg = KeyframeScan(CutAfterDarkGreyLeadIn(), cutWindows: (_, window) => CutFrames(window, cutFrame: 24));
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true }, cache);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var first = await KeyframeCandidates(analyzer, episode);
        var second = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 129.0, 154.0)], first);
        Assert.Equal(first, second);
        Assert.Equal([new CutDecode(128, 130)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_CutStartCachedUnderBlackFrameTimes_IsRead()
    {
        // Every black-frame row sits 0.4 ms after its visual. The cut start keys its cache row on
        // the pages' black-frame times, 28.0004 and 30.0004, so a start stored under them is read
        // and nothing is decoded.
        var ffmpeg = KeyframeScan(CutAfterDarkGreyLeadIn().Select(keyframe => keyframe with { Time = keyframe.Time + 0.0004 }));
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        var episode = CreateQueuedCreditsEpisode();
        cache.Write(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.Cut, 28.0004, 30.0004, [29.0]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true }, cache);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 29.0, 54.0004)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_WithoutBoundaryRefinement_DecodesNoCut()
    {
        // Keyframe-only analysis: the lead-in still trims to the keyframe at 30, and nothing between
        // the keyframes is decoded, though the frames there would move the start to 129.
        var ffmpeg = KeyframeScan(CutAfterDarkGreyLeadIn(), cutWindows: (_, window) => CutFrames(window, cutFrame: 24));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 130.0, 154.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_FailedCutDecode_KeepsTheKeyframeStartUncached()
    {
        // The cut decode fails, so the scene starts on its first page at 30. A failure says nothing
        // about the file, so it is not cached and the next analysis decodes again.
        var ffmpeg = KeyframeScan(CutAfterDarkGreyLeadIn());
        var cache = DatabaseTestHelpers.CreateTempCacheService();
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true }, cache);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var first = await KeyframeCandidates(analyzer, episode);
        var second = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 130.0, 154.0)], first);
        Assert.Equal(first, second);
        Assert.Equal([new CutDecode(128, 130), new CutDecode(128, 130)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_TrimmedLeadInDoesNotComeBackAsCards()
    {
        // A card-like dark grey lead-in of 12 s before 13 s of roll: the trim leaves the scene under
        // the minimum, so there is no accepted scene and the card kinds fall back to the visuals.
        // The lead-in must be content there, or it returns as a card run over lead-in and roll.
        var ffmpeg = KeyframeScan(Keyframes(20, 45, 0.5, (20, 31.5, 95, KeyframeVisuals.DarkGrey), (32, 45, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task DetectCreditsAsync_RollPagesUnderTheMinimum_ExtendTheScene()
    {
        // Dense small lettering takes roll pages under the black minimum, 80 against 85 here (the
        // 0 % content pages set the floor at 0). The pages after the black scene are dark, lettered
        // and colourless, so the scene ends at the last of them, not at its last black keyframe.
        var ffmpeg = KeyframeScan(Keyframes(0, 90, 2, (0, 18, 0, KeyframeVisuals.Content), (20, 40, 95, KeyframeVisuals.Black), (42, 80, 80, KeyframeVisuals.DenseText), (82, 90, 0, KeyframeVisuals.Content)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 120.0, 180.0)], candidates);
    }

    [Theory]
    [InlineData(16.0, 74.0, 244.0, 0.5)] // coloured
    [InlineData(19.0, 74.0, 244.0, 0.0)] // darkest tenth three levels above black
    [InlineData(16.0, 20.0, 60.0, 0.0)] // not lettered: the brightest pixel 44 levels above the darkest tenth
    public async Task DetectCreditsAsync_PagesThatAreNotRollPages_DoNotExtendTheScene(double lumaLow, double lumaHigh, double lumaMax, double saturation)
    {
        // The pages under the black minimum after the scene each fail one roll page clause, so the
        // scene ends at its last black keyframe.
        var ffmpeg = KeyframeScan(Keyframes(0, 90, 2, (0, 18, 0, KeyframeVisuals.Content), (20, 40, 95, KeyframeVisuals.Black), (42, 80, 80, t => new KeyframeVisual(t, 16, lumaLow, lumaHigh, lumaMax, saturation, 0)), (82, 90, 0, KeyframeVisuals.Content)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 120.0, 140.0)], candidates);
    }

    [Fact]
    public async Task DetectCreditsAsync_RollEnd_CountsTowardTheMinimum()
    {
        // The dark grey lead-in trims the scene to 30 to 40, 10 s of black cards, under the 15 s
        // minimum. The roll pages after it take the scene to 60, so it meets the minimum.
        var ffmpeg = KeyframeScan(Keyframes(0, 90, 2, (0, 18, 0, KeyframeVisuals.Content), (20, 28, 95, KeyframeVisuals.DarkGrey), (30, 40, 95, KeyframeVisuals.Black), (42, 60, 80, KeyframeVisuals.DenseText), (62, 90, 0, KeyframeVisuals.Content)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 130.0, 160.0)], candidates);
    }

    [Fact]
    public async Task DetectCreditsAsync_FrameCounterRestart_KeepsTheRoll()
    {
        // The roll above, 100 s later, with the roll's first page on a cut as in the trimmed scene
        // fixture. ffmpeg rebuilds the filtergraph at 100 and at 150, as it does when the video
        // changes resolution, and the blackframe counter starts over at 0 at each, so a page's frame
        // number no longer says where it is. The lead-in still trims the scene to its page at 130,
        // the cut decode still reads the gap before that page, and the scene still ends on its roll
        // pages at 160.
        var pages = Pages(Keyframes(0, 180, 2, (0, 118, 0, KeyframeVisuals.Content), (120, 128, 95, KeyframeVisuals.DarkGrey), (130, 130, 95, KeyframeVisuals.BlackAfterCut), (132, 140, 95, KeyframeVisuals.Black), (142, 160, 80, KeyframeVisuals.DenseText), (162, 180, 0, KeyframeVisuals.Content)))
            .Select((page, i) => page with { Frame = page.Frame with { Frame = page.Frame.Time < 100 ? i : page.Frame.Time < 150 ? i - 50 : i - 75 } })
            .ToArray();
        var ffmpeg = new StubFFmpegService { KeyframeScan = (_, _) => pages, CutWindows = (_, window) => CutFrames(window, cutFrame: 24) };
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 229.0, 260.0)], candidates);
        Assert.Equal([new CutDecode(228, 230)], ffmpeg.Calls);
    }

    [Theory]
    [InlineData("level", 40.0, true)]
    [InlineData("no visual", 40.0, false)]
    [InlineData("dim", 50.0, false)]
    [InlineData("level before dim", 40.0, true)]
    [InlineData("content", 40.0, false)]
    public async Task DetectCreditsAsync_PageJustBeforeAnIntervalStart_IsTheScenesFirstPage(string page, double blackFrameStart, bool pageIsBlackCard)
    {
        // A sparse roll that a blackdetect interval confirms starts on blackdetect's clock, at 40.
        // The page 5 ms before it is the cut keyframe on the scan's clock and the scene's first page,
        // so each fixture gives the same candidates as its twin with the page at 40, except that a
        // card run that starts on the page starts 5 ms earlier. The interval probe covers the roll
        // from its first black page, padded by the 15 s minimum duration. A level page, or one without
        // a visual, keeps the start at 40. A dim page is a lead-in, and the scene starts at the level
        // start frame at 50. A dim start frame after a level page stays in the scene. With a content
        // keyframe in the page's place, the walk starts on the start frame at 50, and the scene keeps
        // 40. No page follows a cut, so nothing is decoded.
        // The card run covers the roll and the grey cards after it, and starts on the page when the
        // scene keeps it as a black card.
        var ffmpeg = PageBeforeTheRoll(page, 39.995);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true, BlackFrameThreshold = 32 });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        var firstBlackPage = page == "content" ? 50 : 39.995;
        Assert.Equal(Expected(39.995), candidates);
        Assert.Equal([new IntervalScan(firstBlackPage - 15, 95, 32, 85)], ffmpeg.Calls);

        // The twin puts the page at blackdetect's start, where it is the start frame.
        var twinFfmpeg = PageBeforeTheRoll(page, 40);
        var twin = CreateKeyframeAnalyzer(twinFfmpeg, new PluginConfiguration { RefineCreditsBoundary = true, BlackFrameThreshold = 32 });
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

    [Theory]
    [InlineData("one page")]
    [InlineData("half the pages")]
    [InlineData("cut then highlight")]
    [InlineData("dark room")]
    public async Task DetectCreditsAsync_RejectedGapDoesNotComeBackAsCards(string visualsKind)
    {
        // The lettering gate rejects the scene, or the lead-in rule rejects a scene dim on every page;
        // its black pages must not become cards through the card kinds' no-scene fallback and come back
        // as a card run. The dark room's pages are card-like, so they would.
        var keyframes = visualsKind switch
        {
            "one page" => Keyframes(20, 54, 0.5, (30, 30, 95, KeyframeVisuals.Black), (20, 54, 95, KeyframeVisuals.BlankBlack)),
            "half the pages" => Keyframes(20, 54, 0.5, (20, 54, 95, t => (t - 20) % 1 == 0 ? KeyframeVisuals.BlankBlack(t) : KeyframeVisuals.Black(t))),
            "dark room" => Keyframes(20, 54, 0.5, (20, 54, 95, KeyframeVisuals.DarkRoom)),
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
    public void TestIsLettered_AnyDensityAndBrightness(double spread, double max, bool expected)
    {
        // Red lettering on black swept across font sizes: the 90th percentile climbs from the
        // background onto the text as the lettering thickens, and every size is lettering. Only the
        // contrast decides: a page whose brightest pixel is under 60 levels above the darkest tenth is blank.
        var visual = new KeyframeVisual(0, 16, 16, 16 + spread, max, 11, 0);

        Assert.Equal(expected, visual.IsLettered());
    }

    [Fact]
    public async Task DetectCreditsAsync_MultipleSparseScenesRequireBlackIntervalSupport()
    {
        // Two sparse scenes, each after a content keyframe, clear the keyframe density gate because
        // the source has only a few keyframes in each run. The large gap separates the runs; the
        // interval probe runs over each scene padded by the 15 s minimum duration, and only the first scene has a
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
        Assert.Equal([new IntervalScan(0, 45, 28, 86), new IntervalScan(95, 145, 28, 86)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_UnconfirmedProbePreservesKeyframeScenes()
    {
        // Two sparse scenes 25 s apart, too far to merge, and an interval that confirms neither.
        // Their probe ranges, padded by the 15 s minimum duration, overlap from 40 to 45, so one
        // scan covers both. The probe changes nothing, and both scenes are candidates.
        Keyframe[] keyframes =
        [
            new(0, 10, KeyframeVisuals.Content(0)),
            .. Keyframes(10, 30, 10, (10, 30, 96, KeyframeVisuals.Black)),
            new(45, 10, KeyframeVisuals.Content(45)),
            .. Keyframes(55, 75, 10, (55, 75, 96, KeyframeVisuals.Black)),
        ];
        var ffmpeg = KeyframeScan(keyframes, intervals: [new BlackInterval(0, 1)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        Assert.Equal([(SegmentSource.BlackFrame, 10.0, 30.0), (SegmentSource.BlackFrame, 55.0, 75.0)], candidates);
        Assert.Equal([new IntervalScan(0, 90, 28, 86)], ffmpeg.Calls);
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
            .. Keyframes(90, 98, 2, (90, 98, 0, KeyframeVisuals.Card)),
            .. Keyframes(100, 130, 2, (100, 130, 96, KeyframeVisuals.Black)),
        ];
        var ffmpeg = KeyframeScan(keyframes, intervals: [new BlackInterval(10, 30)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        Assert.Equal([(SegmentSource.BlackFrame, 10.0, 30.0), (SegmentSource.BlackFrame, 100.0, 130.0), (SegmentSource.KeyframeVisuals, 90.0, 130.0)], candidates);
        Assert.Equal([new IntervalScan(0, 45, 28, 85), new IntervalScan(85, 145, 28, 85)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_EmptyScan_ReturnsNull()
    {
        var ffmpeg = KeyframeScan([]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
        Assert.Equal(1, ffmpeg.KeyframeScanCalls);
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
        // from 20 to 54. No page follows a cut, so nothing is decoded.
        var ffmpeg = KeyframeScan(Keyframes(0, 54, 2, (0, 18, 0, KeyframeVisuals.Card), (20, 54, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 120.0, 154.0), (SegmentSource.KeyframeVisuals, 100.0, 154.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
        Assert.Equal(1, ffmpeg.KeyframeScanCalls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_TooShortScene_ReturnsNull()
    {
        var ffmpeg = KeyframeScan(Keyframes(0, 10, 0.5, (0, 10, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
        Assert.Equal([new IntervalScan(0, 25, 28, 89)], ffmpeg.Calls);
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
        Assert.Equal([new IntervalScan(0, 64.5, 28, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_LowDensitySingleCandidateWithoutIntervalSupportReturnsNull()
    {
        var ffmpeg = KeyframeScan(DarkSceneWithBlackPages(every: 3, percentage: 90));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Empty(candidates);
        Assert.Equal([new IntervalScan(0, 64.5, 28, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectCreditsAsync_StingerSplit_ReturnsBothParts()
    {
        var ffmpeg = KeyframeScan(Keyframes(0, 120, 0.5, (0, 20, 95, KeyframeVisuals.Black), (20.5, 89.5, 30, KeyframeVisuals.Dark), (90, 120, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 1000);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 1000.0, 1020.0), (SegmentSource.BlackFrame, 1090.0, 1120.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Theory]
    [InlineData(72, 367.827)] // the cut lies after the interval start, which stays
    [InlineData(24, 367.45)] // the cut lies before it and starts the scene
    public async Task TestDetectCreditsAsync_BlackIntervalsRecoverSparseKeyframeCredits(int cutFrame, double expectedStart)
    {
        // A sparse roll that a blackdetect interval from 367.827 confirms. Its first page, at 376.46,
        // follows a cut, so the gap from the keyframe at 366.45 is decoded. The scene starts at the
        // earlier of the interval start and the cut frame.
        var ffmpeg = KeyframeScan(
            [
                new(366.45, 15, KeyframeVisuals.Dark(366.45)),
                new(376.46, 96, KeyframeVisuals.BlackAfterCut(376.46)),
                new(386.47, 96, KeyframeVisuals.Black(386.47)),
                new(396.48, 98, KeyframeVisuals.Black(396.48)),
                new(406.49, 99, KeyframeVisuals.Black(406.49)),
                new(416.5, 20, KeyframeVisuals.Dark(416.5)),
            ],
            intervals: [new BlackInterval(367.827, 376.002)],
            cutWindows: (_, window) => CutFrames(window, cutFrame));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 2356.27);

        var candidates = await KeyframeCandidates(analyzer, episode);

        var candidate = Assert.Single(candidates);
        Assert.Equal((SegmentSource.BlackFrame, Math.Round(2356.27 + expectedStart, 6), 2356.27 + 406.49), (candidate.Source, Math.Round(candidate.Start, 6), candidate.End));
        Assert.Equal(
            [
                new IntervalScan(2356.27 + 376.46 - 15, 2356.27 + 406.49 + 15, 28, 87),
                new CutDecode(2356.27 + 366.45, 2356.27 + 376.46),
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
        var ffmpeg = KeyframeScan(Keyframes(0, 50, 10, (0, 0, 10, KeyframeVisuals.Content), (50, 50, 10, KeyframeVisuals.Content), (10, 40, 96, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });
        var episode = CreateQueuedCreditsEpisode();

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 10.0, 40.0)], candidates);

        // The opportunistic interval probe ran but returned nothing; the keyframe scene survives the miss.
        Assert.Equal([new IntervalScan(0, 55, 28, 86)], ffmpeg.Calls);
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
        Assert.Equal([new IntervalScan(100, 135, 28, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_RunsOneIntervalConfirms_AreOneScene()
    {
        // The 30 s between the black keyframes at 70 and 100 splits a sparse roll into keyframe runs
        // at 40 to 70 and 100 to 150, and one blackdetect interval from 40 confirms both. They are
        // one scene, so its black level comes from all its pages and the lifted pages at its head
        // are a lead-in. The scene starts at the level start frame at 100, and there is one
        // candidate.
        var ffmpeg = KeyframeScan(
            Keyframes(0, 200, 10, (40, 70, 100, KeyframeVisuals.LiftedBlack), (100, 150, 100, KeyframeVisuals.Black)),
            intervals: [new BlackInterval(40, 110)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = true });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        Assert.Equal([(SegmentSource.BlackFrame, 100.0, 150.0)], candidates);
        Assert.Equal([new IntervalScan(25, 165, 28, 85)], ffmpeg.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetectCreditsAsync_DenseSceneUnderAnInterval_KeepsItsRuns(bool oneInterval)
    {
        // A dense roll at 20 to 40 and a sparse run at 70 to 90. The dense scene keeps its own start
        // and its runs, and an interval makes a scene only of the runs outside it. One interval over
        // both, through the content between them, gives a second scene from 19, which the credits
        // pass joins to the first. An interval over each gives a second scene from 65, and no copy
        // of the dense scene from 19.
        double[] times = [.. Times(0, 62, 2), 70, 80, 90, .. Times(92, 120, 2)];
        var ffmpeg = KeyframeScan(
            times.Select(t => t switch
            {
                >= 20 and <= 40 or >= 70 and <= 90 => new Keyframe(t, 100, KeyframeVisuals.Black(t)),
                _ => new Keyframe(t, 0, KeyframeVisuals.Content(t)),
            }),
            intervals: oneInterval ? [new BlackInterval(19, 90)] : [new BlackInterval(19, 40), new BlackInterval(65, 90)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode());

        (SegmentSource, double, double)[] expected = oneInterval
            ? [(SegmentSource.BlackFrame, 20, 40), (SegmentSource.BlackFrame, 19, 90)]
            : [(SegmentSource.BlackFrame, 20, 40), (SegmentSource.BlackFrame, 65, 90)];
        Assert.Equal(expected, candidates);
        Assert.Equal([new IntervalScan(5, 105, 28, 85)], ffmpeg.Calls);
    }

    [Fact]
    public async Task TestDetectIntervalSupportedCreditScenes_UsesIntervalEndForDurationAndBounds()
    {
        BlackFrame[] frames =
        [
            new(96, 10, 10),
            new(96, 12, 12),
        ];

        var scenes = await ScanOf(frames, blackMinimum: 85, sceneChange: 96).ScenesAsync(15, ProbeFinding(new BlackInterval(5, 25)));

        var scene = Assert.Single(scenes);
        Assert.Equal(5, scene.StartTime);
        Assert.Equal(25, scene.EndTime);
    }

    [Fact]
    public async Task TestDetectIntervalSupportedCreditScenes_AnchorsTailSupportToInterval()
    {
        var frames = new List<BlackFrame>();
        for (var time = 0; time <= 100; time += 10)
        {
            frames.Add(new BlackFrame(96, time, time));
        }

        var scenes = await ScanOf(frames, blackMinimum: 85, sceneChange: 96).ScenesAsync(15, ProbeFinding(new BlackInterval(90, 120)));

        var scene = Assert.Single(scenes);
        Assert.Equal(90, scene.StartTime);
        Assert.Equal(120, scene.EndTime);
        Assert.Equal(9, scene.StartIndex);
        Assert.Equal(10, scene.EndIndex);
    }

    [Fact]
    public async Task TestDetectIntervalSupportedCreditScenes_PrefersLongerOverlappingInterval()
    {
        BlackFrame[] frames =
        [
            new(96, 10, 10),
            new(96, 12, 11),
        ];

        // The first overlapping interval is too short to satisfy the minimum duration; a later, longer
        // overlapping interval must still be used instead of rejecting the candidate.
        var scenes = await ScanOf(frames, blackMinimum: 85, sceneChange: 96).ScenesAsync(15, ProbeFinding(new BlackInterval(9, 13), new BlackInterval(9, 40)));

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
    public async Task TestDetectCreditsAsync_RefinesBoundaryByDefault()
    {
        // The roll's first page at 10 follows a cut from the dark shot at 8, and the frame at 9.25
        // carries it.
        var ffmpeg = KeyframeScan(
            [.. Keyframes(0, 8, 0.5, (0, 8, 30, KeyframeVisuals.Dark)), .. Keyframes(10, 30, 0.5, (10, 10, 95, KeyframeVisuals.BlackAfterCut), (10.5, 30, 95, KeyframeVisuals.Black))],
            cutWindows: (_, window) => CutFrames(window, cutFrame: 30));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 109.25, 130.0)], candidates);
        Assert.Equal([new CutDecode(108, 110)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_EarlierSceneUnderTheMinimum_IsNotProbed()
    {
        // 10 to 23 is 13 s, under the minimum, though a cut in the 2 s keyframe gap before it could
        // bring it there, and 60 to 80 is a roll. Both first pages follow a cut, but only the roll is
        // a scene, so only its gap is decoded and it is the only candidate.
        var ffmpeg = KeyframeScan(
        [
            .. Keyframes(0, 8, 0.5, (0, 8, 30, KeyframeVisuals.Dark)),
            .. Keyframes(10, 58, 0.5, (10, 10, 95, KeyframeVisuals.BlackAfterCut), (10.5, 23, 95, KeyframeVisuals.Black), (23.5, 58, 30, KeyframeVisuals.Dark)),
            .. Keyframes(60, 80, 0.5, (60, 60, 95, KeyframeVisuals.BlackAfterCut), (60.5, 80, 95, KeyframeVisuals.Black)),
        ]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 160.0, 180.0)], candidates);
        Assert.Equal([new CutDecode(158, 160)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_SceneUnderTheMinimum_GoesToIntervalRecovery()
    {
        // The only keyframe scene, 14 to 24, is under the minimum. Interval recovery finds the
        // credits from 8.
        var ffmpeg = KeyframeScan(
            [.. Keyframes(0, 8, 0.5, (0, 8, 30, KeyframeVisuals.Dark)), .. Keyframes(14, 24, 0.5, (14, 24, 95, KeyframeVisuals.Black))],
            intervals: [new BlackInterval(8, 24)]);
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);
        var episode = CreateQueuedCreditsEpisode(creditsFingerprintStart: 100);

        var candidates = await KeyframeCandidates(analyzer, episode);

        Assert.Equal([(SegmentSource.BlackFrame, 108.0, 124.0)], candidates);
        Assert.Equal([new IntervalScan(100, 139, 28, 89)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_DimFirstPageBeforeAPageWithoutAVisual_IsTheLeadIn()
    {
        // The roll's first page is dim and its second page has no visual. A page without a visual
        // ends the lead-in walk, since nothing says it is dim. So the dim page alone is the lead-in,
        // and the scene starts on the page without a visual at 22, not on the first level page at
        // 24. Without a visual that page has no frame difference, so no cut is decoded before it.
        var ffmpeg = KeyframeScan(Keyframes(0, 70, 2, (20, 20, 95, KeyframeVisuals.DarkGrey), (22, 22, 95, _ => null), (24, 54, 95, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.BlackFrame, 122.0, 154.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_DenseTextWithoutAnAcceptedScene_IsContent()
    {
        // Grey cards run for 10 s, then dense lettering on black at 88 percent runs for 12 s. That is
        // too short for a black scene and blackdetect confirms nothing, so no scene is accepted and
        // the card kinds fall back to the visuals. Dense text is lettered but not card-like, so it is
        // content, not a card or a black card. The cards alone are under the minimum.
        var ffmpeg = KeyframeScan(Keyframes(0, 70, 2, (30, 40, 0, KeyframeVisuals.Card), (42, 54, 88, KeyframeVisuals.DenseText)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Empty(candidates);
        Assert.Equal([new IntervalScan(127, 169, 28, 85)], ffmpeg.Calls);
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
        Assert.Equal([new IntervalScan(25, 95, 28, 85)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_SceneOnlyPastTheWindowEnd_LeavesNoCardFallback()
    {
        // The credits window ends 60 s in. The black rows run to the end of the file and have no
        // visuals past the window. A roll that lies only there, from 82 to 110, is an accepted scene,
        // since without visuals it has no lead-in and no lettering verdict. Inside the window, grey
        // cards and then 12 s of black roll pages are too short for a black scene. Accepted is not
        // empty, so the card kinds do not fall back to the visuals. The black pages outside every
        // accepted scene are content, the cards alone are under the minimum, and there is no card run.
        var ffmpeg = KeyframeScan(Keyframes(
            0,
            120,
            2,
            (32, 40, 0, KeyframeVisuals.Card),
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
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_CardBelowTheBlackMinimumInARejectedGap_StaysACard()
    {
        // A blank black gap between acts holds a grey card at 38, below the black minimum, among its
        // last pages. Grey cards follow from 42. The lettering gate rejects the gap. A rejected range
        // makes only its black pages content, so the grey card stays a card and starts the card run.
        // Without it the run would be 12 s, under the minimum.
        var ffmpeg = KeyframeScan(Keyframes(0, 70, 2, (38, 38, 0, KeyframeVisuals.Card), (20, 40, 100, KeyframeVisuals.BlankBlack), (42, 54, 0, KeyframeVisuals.Card)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.KeyframeVisuals, 138.0, 154.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
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
            (38, 48, 0, KeyframeVisuals.Card),
            (80, 110, 100, KeyframeVisuals.Black)));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.BlackFrame, 180.0, 210.0), (SegmentSource.KeyframeVisuals, 126.0, 148.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_CardRun_StartsOnTheCut()
    {
        // Content to 28, then grey cards from 30 to 60, the first differing from the content before
        // it by 40. No page is black, so there is no black scene, and the card run starts on the cut
        // frame of the gap before its first page.
        var ffmpeg = KeyframeScan(
            Keyframes(0, 70, 2, (30, 30, 0, t => KeyframeVisuals.Card(t) with { FrameDifference = 40 }), (32, 60, 0, KeyframeVisuals.Card)),
            cutWindows: (_, window) => CutFrames(window, cutFrame: 36));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg);

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.KeyframeVisuals, 129.5, 160.0)], candidates);
        Assert.Equal([new CutDecode(128, 130)], ffmpeg.Calls);
    }

    [Fact]
    public async Task DetectCreditsAsync_CardRunWithoutBoundaryRefinement_DecodesNoCut()
    {
        // The card run from DetectCreditsAsync_CardRun_StartsOnTheCut, analyzed keyframe-only: it
        // starts on its first page at 30 and nothing between the keyframes is decoded.
        var ffmpeg = KeyframeScan(
            Keyframes(0, 70, 2, (30, 30, 0, t => KeyframeVisuals.Card(t) with { FrameDifference = 40 }), (32, 60, 0, KeyframeVisuals.Card)),
            cutWindows: (_, window) => CutFrames(window, cutFrame: 36));
        var analyzer = CreateKeyframeAnalyzer(ffmpeg, new PluginConfiguration { RefineCreditsBoundary = false });

        var candidates = await KeyframeCandidates(analyzer, CreateQueuedCreditsEpisode(creditsFingerprintStart: 100));

        Assert.Equal([(SegmentSource.KeyframeVisuals, 130.0, 160.0)], candidates);
        Assert.Empty(ffmpeg.Calls);
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
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YDIF=0
            [Parsed_metadata_2 @ 0x0] frame:1    pts:20480   pts_time:2
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YAVG=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YHIGH=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATLOW=0
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=0
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YDIF=101.5
            """;

        var visuals = FFmpegOutputParser.ParseKeyframeVisuals(raw);

        Assert.Equal(2, visuals.Length);
        Assert.Equal(new KeyframeVisual(0.0, 16, 60, 200, 235, 108.199, 0), visuals[0]);
        Assert.Equal(new KeyframeVisual(2.0, 16, 16, 16, 235, 0, 101.5), visuals[1]);
    }

    [Fact]
    public void TestParseKeyframeVisuals_ReadsTheFrameDifferenceOfARecordedBlock()
    {
        // One block as jellyfin-ffmpeg 8.1 prints it for the keyframe scan of the credits fixture:
        // the cut to the blank black page at 300 s differs from the keyframe before by 45. UDIF and
        // VDIF are the chroma differences, which the parser leaves alone.
        const string raw = """
            [Parsed_metadata_2 @ 00000191857e6dc0] frame:30   pts:3840000 pts_time:300
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.YLOW=16
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.YAVG=16
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.YHIGH=16
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.YMAX=16
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.UMIN=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.ULOW=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.UAVG=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.UHIGH=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.UMAX=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.VMIN=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.VLOW=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.VAVG=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.VHIGH=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.VMAX=128
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.SATMIN=0
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.SATLOW=0
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.SATAVG=0
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.SATHIGH=0
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.SATMAX=0
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.HUEMED=180
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.HUEAVG=180
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.YDIF=45
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.UDIF=37
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.VDIF=47
            [Parsed_metadata_2 @ 00000191857e6dc0] lavfi.signalstats.YBITDEPTH=1
            """;

        var visual = Assert.Single(FFmpegOutputParser.ParseKeyframeVisuals(raw));

        Assert.Equal(new KeyframeVisual(300, 16, 16, 16, 16, 0, 45), visual);
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
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=12.5
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YDIF=3
            [Parsed_metadata_2 @ 0x0] frame:1 pts:1 pts_time:7
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=128
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=12.5
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YDIF=3
            [Parsed_metadata_2 @ 0x0] frame:2 pts:2 pts_time:9
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=128
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YHIGH=128
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
            """;

        var visual = Assert.Single(FFmpegOutputParser.ParseKeyframeVisuals(raw));

        Assert.Equal(new KeyframeVisual(5.0, 16, 128, 128, 235, 12.5, 3), visual);
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
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=3.2e+01
            [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YDIF=1.5e+01
            [Parsed_metadata_2 @ 0x0] frame:1 pts:1 pts_time:2
            """;

        var visual = Assert.Single(FFmpegOutputParser.ParseKeyframeVisuals(raw));

        Assert.Equal(new KeyframeVisual(1e-05, 16, 128, 128, 235, 32, 15), visual);
    }

    // Each row: a keyframe visual and whether it is card-like.
    public static TheoryData<KeyframeVisual, bool> CardLikeCases => new()
    {
        { KeyframeVisuals.Card(0), true },
        { KeyframeVisuals.WhiteCard(0), true },

        // Spread 9: the background is not dominant.
        { new KeyframeVisual(0, 16, 128, 137, 235, 30, 0), false },

        // Spread 8 with contrast 60 on both sides: both boundaries inclusive.
        { new KeyframeVisual(0, 68, 128, 136, 196, 30, 0), true },

        // Contrast 59: a bare wall or a fade.
        { new KeyframeVisual(0, 69, 128, 128, 187, 30, 0), false },

        { KeyframeVisuals.BlankBlack(0), false },
        { KeyframeVisuals.Dark(0), false },
        { KeyframeVisuals.FlatWithSubject(0), false },

        // Card at the exclusive saturation maximum.
        { KeyframeVisuals.Card(0, saturation: 96), false },
    };

    [Theory]
    [MemberData(nameof(CardLikeCases))]
    public void TestIsCardLike(KeyframeVisual visual, bool expected)
    {
        Assert.Equal(expected, visual.IsCardLike());
    }

    // Each row: a keyframe scan with no black page, the minimum credit duration, and the expected (Start, End) or null.
    public static TheoryData<KeyframePage[], int, (double Start, double End)?> CardCreditsCases => new()
    {
        // Clean dense card run -> unchanged.
        { Pages(Seq(54, 2, (30, 54))), 15, (30, 54) },

        // Mid-body ident interlude (6s of non-card bracketed by dense cards) -> preserved.
        { Pages(Seq(60, 2, (0, 28), (36, 60))), 15, (0, 60) },

        // Interlude near the end (cards resume densely after) -> preserved.
        { Pages(Seq(60, 2, (0, 48), (56, 60))), 15, (0, 60) },

        // Sparse all-card credits (8s GOP) -> kept (100% density).
        { Pages(Seq(40, 8, (0, 40))), 15, (0, 40) },

        // Uniform sparse long-GOP credits (12s cadence) -> kept as one run.
        { Pages(Seq(48, 12, (0, 48))), 15, (0, 48) },

        // All-card run whose 21s cadence exceeds the fixed bridge, with nothing non-card between
        // -> stays one run instead of splitting into one-frame runs that each miss the minimum.
        { Pages(Seq(63, 21, (0, 63))), 60, (0, 63) },

        // Two real runs separated by a long gap -> latest selected.
        { Pages(Seq(80, 2, (0, 20), (60, 80))), 15, (60, 80) },

        // An earlier dense run (0-20) must not capture or extend into a later long-GOP credit run
        // (60-96, 12s cadence): real content separates the groups, so the latest run is returned.
        { Pages(NotBlack([.. Cards(0, 20, 2), .. Busy(22, 58, 2), .. Cards(60, 96, 12)])), 15, (60, 96) },

        // Dense non-card content, then static cards on a 12s-keyframe source: grouping must key off the
        // card cadence, not the content cadence, or every 12s card gap splits the run.
        { Pages(NotBlack([.. Busy(0, 58, 2), .. Cards(60, 96, 12)])), 15, (60, 96) },

        // Sparse isolated cards bridged across busy 2s content (brief dense head, then a lone card
        // every 8s) -> rejected by the card-density floor: most keyframes in the span are busy
        // content, so this reads as normal content with occasional static shots, not a card sequence.
        { Pages(Seq(54, 2, (0, 6), (14, 14), (22, 22), (30, 30), (38, 38), (46, 46), (54, 54))), 15, null },

        // Two card-like keyframes 18s apart with busy keyframes between them -> not credits.
        { Pages(Seq(18, 2, (0, 0), (18, 18))), 15, null },

        // A final card 4s after the body, busy content between -> kept in the run.
        { Pages(Seq(44, 2, (0, 40), (44, 44))), 15, (0, 44) },

        // Only 10s of card -> below the minimum duration.
        { Pages(Seq(40, 2, (30, 40))), 15, null },

        // Dark (low luma) but detailed content spreads wide within the dark range, like a night scene -> not a card.
        // Its percentiles put it between a tenth and nine tenths black, so its rows sit at 50, under the minimum.
        { Pages(Times(0, 58, 2).Select(t => new Keyframe(t, 50, KeyframeVisuals.Dark(t)))), 15, null },

        // Uniform but vividly saturated frames are excluded on purpose (see KeyframeVisualTraits).
        { Pages(NotBlack(CreateCardCreditVisuals(cardStart: 0, cardEnd: 20, cardSaturation: 200))), 15, null },

        // All busy content -> null.
        { Pages(Seq(60, 2)), 15, null },
    };

    [Theory]
    [MemberData(nameof(CardCreditsCases))]
    public void TestCardRunFinder_FindCreditRange(KeyframePage[] pages, int minimumDuration, (double Start, double End)? expected)
    {
        var range = CardRunFinder.FindCreditRange(KeyframeAnalyzer.StampCardKinds(new KeyframeScan(pages, blackMinimum: 85, sceneChange: 96), accepted: [], rejected: []), minimumDuration);

        if (expected is null)
        {
            Assert.Null(range);
            return;
        }

        Assert.NotNull(range);
        Assert.Equal(expected.Value.Start, range.Start);
        Assert.Equal(expected.Value.End, range.End);
    }

    // Each row: the keyframe scan (one black row and one visual per keyframe); the black scenes the
    // black-frame rules accepted, empty when they found no credits; the lead-ins and scenes they
    // rejected; the expected (Start, End) or null.
    public static TheoryData<KeyframePage[], (double Start, double End)[], (double Start, double End)[], (double Start, double End)?> BlackKeyframeCases
    {
        get
        {
            var data = new TheoryData<KeyframePage[], (double Start, double End)[], (double Start, double End)[], (double Start, double End)?>();

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
            data.Add(Pages(epilogue), [(68, 118)], [], null);

            // Grey cards then a roll: the black cards extend the run. Every black-frame row sits 0.4 ms
            // after its visual, as the two ffmpeg filters can print them, and the run is timed on the
            // rows. The accepted scene ends at 80, so the last roll page belongs to it through the
            // membership pad.
            var cardsThenRoll = Keyframes(0, 80, 2, (0, 40, 0, KeyframeVisuals.Card), (42, 80, 100, KeyframeVisuals.Black));
            data.Add(Pages(cardsThenRoll.Select(keyframe => keyframe with { Time = keyframe.Time + 0.0004 })), [(42, 80)], [], (0.0004, 80.0004));

            // Cards 15 s apart on the visual clock, which FFmpeg before 7.0 rounds to six significant
            // digits, with their black-frame rows a fraction of a millisecond later: 14.9998 s apart on
            // the black-frame clock the run is timed on, so the run falls short of the 15 s minimum.
            data.Add(
                Pages(
                [
                    .. Keyframes(90, 98, 2),
                    new(100.0004, 0, KeyframeVisuals.Card(100)),
                    new(105.0003, 0, KeyframeVisuals.Card(105)),
                    new(110.0003, 0, KeyframeVisuals.Card(110)),
                    new(115.0002, 0, KeyframeVisuals.Card(115)),
                    .. Keyframes(117, 125, 2),
                ]),
                [],
                [],
                null);

            // Pages without a visual are invisible to the card finder: between two card spans 24 s
            // apart they neither break the run nor count against its density.
            data.Add(
                Pages([.. Keyframes(0, 20, 2, (0, 20, 0, KeyframeVisuals.Card)), .. Keyframes(22, 42, 2, (22, 42, 0, _ => null)), .. Keyframes(44, 64, 2, (44, 64, 0, KeyframeVisuals.Card))]),
                [],
                [],
                (0, 64));

            // Grey, black, grey: one run.
            data.Add(Pages(Keyframes(0, 80, 2, (0, 20, 0, KeyframeVisuals.Card), (22, 60, 100, KeyframeVisuals.Black), (62, 80, 0, KeyframeVisuals.Card))), [(22, 60)], [], (0, 80));

            // A blank black page in the middle of a roll inside an accepted scene: black is black there,
            // so the page is not content and the run covers the roll and the cards after it.
            data.Add(Pages(Keyframes(0, 40, 2, (10, 10, 100, KeyframeVisuals.BlankBlack), (0, 18, 100, KeyframeVisuals.Black), (20, 40, 0, KeyframeVisuals.Card))), [(0, 18)], [], (0, 40));

            // A grey vanity card in the middle of a roll, inside the accepted scene that merged across
            // it: a black card like the roll pages around it, so the roll still has no card density.
            data.Add(Pages(Keyframes(0, 60, 2, (28, 32, 0, KeyframeVisuals.Card), (0, 60, 100, KeyframeVisuals.Black))), [(0, 60)], [], null);

            // Roll only: as an accepted black scene there is no card density and the roll is the
            // black-frame candidate's; with no candidate the visuals alone decide and it is recovered here.
            var rollOnly = Pages(Keyframes(0, 60, 2, (0, 60, 100, KeyframeVisuals.Black)));
            data.Add(rollOnly, [(0, 60)], [], null);
            data.Add(rollOnly, [], [], (0, 60));

            // Grey cards, a roll, then a solid white frame on a fully black row inside the accepted
            // scene. No scan pairs the two; the contradiction is deliberate. A white screen is content
            // even when the black-frame evidence misclassifies it, so it does not extend the run.
            data.Add(Pages(Keyframes(0, 60, 2, (0, 30, 0, KeyframeVisuals.Card), (32, 58, 100, KeyframeVisuals.Black), (60, 60, 100, KeyframeVisuals.WhiteScreen))), [(32, 60)], [], (0, 58));

            // Short grey cards then a short roll, each below the minimum on its own, qualify together.
            data.Add(Pages(Keyframes(0, 24, 2, (0, 10, 0, KeyframeVisuals.Card), (12, 24, 100, KeyframeVisuals.Black))), [], [], (0, 24));

            // Sparse black cards on a 21 s cadence that the black-frame rules could not confirm:
            // recovered as the old fallback did.
            data.Add(Pages(Keyframes(0, 63, 21, (0, 63, 100, KeyframeVisuals.Black))), [], [], (0, 63));

            // A dark grey lead-in at 90 percent black before a full-black roll. Its pages are black and
            // pass the card test, but the accepted black scene starts at the roll, so the lead-in is
            // content here too and the run starts at the roll.
            data.Add(Pages(Keyframes(0, 100, 2, (0, 40, 90, KeyframeVisuals.DarkGrey), (42, 78, 100, KeyframeVisuals.Black), (80, 100, 0, KeyframeVisuals.Card))), [(42, 78)], [], (42, 100));

            // The same lead-in, trimmed, before a roll too short to be a candidate, then grey cards.
            // With no accepted scene the visuals decide, but the lead-in's black pages are content
            // there too, so the run starts at the roll.
            data.Add(Pages(Keyframes(0, 50, 2, (0, 20, 90, KeyframeVisuals.DarkGrey), (22, 34, 100, KeyframeVisuals.Black), (36, 50, 0, KeyframeVisuals.Card))), [], [(0, 20)], (22, 50));

            // Grey cards then a roll whose tail lies in a rejected range too. There the rejected range
            // wins for a black page only: the roll pages are content and end the run, but a card-like
            // page below the black threshold stays a black card of the accepted scene.
            data.Add(Pages(Keyframes(0, 60, 2, (0, 30, 0, KeyframeVisuals.Card), (32, 50, 100, KeyframeVisuals.Black), (52, 52, 0, KeyframeVisuals.Card), (54, 60, 100, KeyframeVisuals.Black))), [(32, 60)], [(52, 60)], (0, 52));

            // The same roll without the grey cards: the card-like page is a black card, not a card, so
            // the run has no card density.
            data.Add(Pages(Keyframes(32, 60, 2, (32, 50, 100, KeyframeVisuals.Black), (52, 52, 0, KeyframeVisuals.Card), (54, 60, 100, KeyframeVisuals.Black))), [(32, 60)], [(52, 60)], null);

            // Sparse grey cards on a 12 s cadence then a dense roll: one run from the first grey card.
            data.Add(Pages([.. Keyframes(0, 24, 12, (0, 24, 0, KeyframeVisuals.Card)), .. Keyframes(36, 80, 2, (36, 80, 100, KeyframeVisuals.Black))]), [(36, 80)], [], (0, 80));

            // Dark detailed keyframes at 89 percent are black at the minimum of 85, but their luma spreads
            // too wide for a card. With no accepted scene the visuals alone decide, so they are content
            // and stay in the density ratio: 13 cards among 31 keyframes fail the floor.
            data.Add(Pages(Times(0, 60, 2).Select(t => t % 10 is 0 or 4 ? new Keyframe(t, 0, KeyframeVisuals.Card(t)) : new Keyframe(t, 89, KeyframeVisuals.Dark(t)))), [], [], null);

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
                (52, 80, 0, KeyframeVisuals.Card));
            data.Add(Pages(blackFlashes), [(30, 50)], [], (30, 80));

            // Black page, grey card, busy frame repeating, with no black-frame candidate: the visuals
            // alone decide, as the old fallback did, and two cards in three keep the run.
            data.Add(Pages(Times(0, 60, 2).Select(t => t % 6 == 0 ? new Keyframe(t, 100, KeyframeVisuals.Black(t)) : new Keyframe(t, 0, t % 6 == 2 ? KeyframeVisuals.Card(t) : KeyframeVisuals.Content(t)))), [], [], (0, 60));

            // Red lettering on black raises the mean saturation, not the background's: a card run
            // through the no-scene fallback like white lettering would be.
            data.Add(Pages(Keyframes(0, 60, 2, (0, 60, 100, KeyframeVisuals.RedText))), [], [], (0, 60));

            // Grey cards then a short roll, content, then a separate longer roll: the black-frame
            // analyzer accepts both scenes and returns each as a candidate. The short roll's pages are
            // black cards, so the grey cards and the short roll make one run that qualifies on the
            // grey cards' density. The longer roll has no card density and stays that analyzer's.
            data.Add(Pages(Keyframes(0, 140, 2, (0, 10, 0, KeyframeVisuals.Card), (12, 30, 100, KeyframeVisuals.Black), (100, 140, 100, KeyframeVisuals.Black))), [(12, 30), (100, 140)], [], (0, 30));

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BlackKeyframeCases))]
    public void TestCardRunFinder_BlackKeyframes(KeyframePage[] pages, (double Start, double End)[] accepted, (double Start, double End)[] rejected, (double Start, double End)? expected)
    {
        var kinds = KeyframeAnalyzer.StampCardKinds(new KeyframeScan(pages, blackMinimum: 85, sceneChange: 96), Ranges(accepted), Ranges(rejected));
        var range = CardRunFinder.FindCreditRange(kinds, minimumDuration: 15);

        if (expected is null)
        {
            Assert.Null(range);
            return;
        }

        Assert.NotNull(range);
        Assert.Equal(expected.Value.Start, range.Start);
        Assert.Equal(expected.Value.End, range.End);

        static TimeRange[] Ranges((double Start, double End)[] ranges) => [.. ranges.Select(range => new TimeRange(range.Start, range.End))];
    }

    // ── Fingerprint-based integration tests ──────────────────────────────

    [Fact]
    public async Task TestFingerprint_Alt3_CleanCredits_SingleScene()
    {
        // alt-3: fewest frames (211), clean credits, no transition-frame shift.
        var frames = ParseFingerprintFile("blackframe-alt-3");
        var (minimum, sceneChange) = BlackFrameThresholdHelper.NormalizeThreshold(frames, 85);

        var scenes = await ScanOf(frames, minimum, sceneChange).ScenesAsync(15, ProbeFinding());

        // Single credit block, no transition-frame shift (no frame reaches sceneChange=95)
        Assert.Single(scenes);
        Assert.Equal(516.012, scenes[0].StartTime);
        Assert.Equal(584.33, scenes[0].EndTime);
    }

    [Fact]
    public async Task TestFingerprint_Alt4_DarkShow_TransitionFrameShift()
    {
        // alt-4: dark show where 31% of frames are >=85% pblack.
        // Transition-frame search shifts the last scene's start forward to skip dark-but-not-credits content.
        var frames = ParseFingerprintFile("blackframe-alt-4");
        var (minimum, sceneChange) = BlackFrameThresholdHelper.NormalizeThreshold(frames, 85);

        var scenes = await ScanOf(frames, minimum, sceneChange).ScenesAsync(15, ProbeFinding());

        Assert.True(scenes.Count >= 4);

        // The real credits are the last scene.
        // Before transition-frame search: start=422.843s
        // After: first frame >= sceneChange (95) shifts start to 463.425s
        var credits = scenes[^1];
        Assert.Equal(463.425, credits.StartTime);
        Assert.Equal(558.479, credits.EndTime);
    }

    [Fact]
    public async Task TestFingerprint_Alt5_MidCreditScene_TwoSeparateBlocks()
    {
        // alt-5: mid-credit stinger creates two separate credit blocks. The non-zero
        // 1st-percentile floor (25) scales the thresholds to minimum=88, sceneChange=96.
        var frames = ParseFingerprintFile("blackframe-alt-5");
        var (minimum, sceneChange) = BlackFrameThresholdHelper.NormalizeThreshold(frames, 85);

        var scenes = await ScanOf(frames, minimum, sceneChange).ScenesAsync(15, ProbeFinding());

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
    private static async Task<(Segment? Credits, IReadOnlyList<Call> Calls)> RunChapterAnalysisAsync(TimeRange[] blackRanges, double[] chapterStarts, double duration)
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

        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Duration = duration,
            CreditsFingerprintStart = 2000,
            Chapters = [.. chapterStarts.Select(start => new ChapterInfo { StartPositionTicks = TimeSpan.FromSeconds(start).Ticks })],
        };

        return (await analyzer.TryAnalyzeChaptersAsync(episode, 85, 28, CancellationToken.None), ffmpeg.Calls);
    }

    /// <summary>
    /// Every candidate the keyframe analyzer returns, by source. The analyzers here run on the default
    /// black minimum of 85, blackframe threshold of 28, 15 s minimum duration and card detection, so a
    /// stray card run fails a test as a wrong black-frame candidate does.
    /// </summary>
    private static async Task<List<(SegmentSource Source, double Start, double End)>> KeyframeCandidates(KeyframeAnalyzer analyzer, QueuedEpisode episode)
        => [.. (await analyzer.DetectCreditsAsync(episode, CancellationToken.None)).Select(c => (c.Source, c.Segment.Start, c.Segment.End))];

    private static KeyframeScan ScanOf(IEnumerable<BlackFrame> frames, int blackMinimum, int sceneChange)
        => new([.. frames.Select(frame => new KeyframePage(frame, null))], blackMinimum, sceneChange);

    private static Func<IReadOnlyList<CreditScene>, Task<BlackInterval[]>> ProbeFinding(params BlackInterval[] intervals)
        => _ => Task.FromResult(intervals);

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
    /// Creates a stub whose keyframe scan reports <paramref name="keyframes"/>, whose blackdetect
    /// scans return <paramref name="intervals"/> and whose cut decodes return what
    /// <paramref name="cutWindows"/> returns, or fail.
    /// </summary>
    private static StubFFmpegService KeyframeScan(
        IEnumerable<Keyframe> keyframes,
        BlackInterval[]? intervals = null,
        Func<QueuedEpisode, TimeRange, KeyframeVisual[]?>? cutWindows = null)
    {
        var pages = Pages(keyframes);
        return new()
        {
            KeyframeScan = (_, _) => pages,
            BlackIntervals = (_, _, _, _) => intervals ?? [],
            CutWindows = cutWindows ?? ((_, _) => null),
        };
    }

    /// <summary>
    /// Keyframes two seconds apart from 20 to 54: a dark grey lead-in to 28, then a roll whose first
    /// page, at 30, follows a cut.
    /// </summary>
    private static Keyframe[] CutAfterDarkGreyLeadIn()
        => Keyframes(20, 54, 2, (20, 28, 95, KeyframeVisuals.DarkGrey), (30, 30, 95, KeyframeVisuals.BlackAfterCut), (32, 54, 95, KeyframeVisuals.Black));

    /// <summary>
    /// The frames a cut decode reports for <paramref name="window"/>: 24 a second from its start to
    /// its end, each differing from the frame before by 1, except the first, which differs by 0, and
    /// the frame <paramref name="cutFrame"/> after the start, which carries the cut's difference of 40.
    /// </summary>
    private static KeyframeVisual[] CutFrames(TimeRange window, int cutFrame)
        => [.. Enumerable.Range(0, (int)Math.Round(window.Duration * 24)).Select(i =>
            KeyframeVisuals.Black(window.Start + (i / 24.0)) with { FrameDifference = i == 0 ? 0 : i == cutFrame ? 40 : 1 })];

    /// <summary>
    /// Keyframes 0.5 s apart from 0 to 49.5: dark shots at 30 percent black, with a black roll page at
    /// <paramref name="percentage"/> on the first keyframe and every <paramref name="every"/> keyframes
    /// after it.
    /// </summary>
    private static IEnumerable<Keyframe> DarkSceneWithBlackPages(int every, int percentage)
        => Times(0, 49.5, 0.5).Select((t, i) => i % every == 0 ? new Keyframe(t, percentage, KeyframeVisuals.Black(t)) : new Keyframe(t, 30, KeyframeVisuals.Dark(t)));

    /// <summary>
    /// Keyframes ten seconds apart from 0 to 120: content, then a roll that is black from the page at
    /// <paramref name="pageTime"/> to 80, then grey cards. The roll is sparse, so the analyzer probes
    /// it with blackdetect, and the interval from 40 to 80 makes it a scene that starts on
    /// blackdetect's clock. <paramref name="page"/> gives the page a level visual, a dim one or none;
    /// "level before dim" is a level page before a dim start frame at 50; "content" makes the page a
    /// content keyframe, so the roll's first black page is the start frame at 50.
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

    /// <summary>
    /// Keyframes 0.5s apart with a per-index black percentage.
    /// </summary>
    private static BlackFrame[] CreateFrames(int count, Func<int, int> percentage)
        => [.. Enumerable.Range(0, count).Select(i => new BlackFrame(percentage(i), i * 0.5, i))];

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
