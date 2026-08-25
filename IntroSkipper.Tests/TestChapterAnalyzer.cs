// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TestChapterAnalyzer
{
    [Theory]
    [InlineData("Opening")]
    [InlineData("OP")]
    [InlineData("Intro")]
    [InlineData("Intro:")]
    [InlineData("Intro Start")]
    [InlineData("Introduction")]
    public void TestIntroductionExpression(string chapterName)
    {
        var introChapter = FindChapter(chapterName, AnalysisMode.Introduction);

        Assert.NotNull(introChapter);
        Assert.Equal(60, introChapter.Start);
        Assert.Equal(90, introChapter.End);
    }

    [Theory]
    [InlineData("End Credits")]
    [InlineData("Ending")]
    [InlineData("Credit start")]
    [InlineData("Closing Credits")]
    [InlineData("Credits")]
    [InlineData("Credits:")]
    public void TestEndCreditsExpression(string chapterName)
    {
        var creditsChapter = FindChapter(chapterName, AnalysisMode.Credits);

        Assert.NotNull(creditsChapter);
        Assert.Equal(1890, creditsChapter.Start);
        Assert.Equal(2000, creditsChapter.End);
    }

    [Fact]
    public void BuildRecapFromBlackFrames_ReturnsSegmentFromStartToLatestFrameInRange()
    {
        var episodeId = Guid.NewGuid();
        var frames = new List<BlackFrame>
        {
            new(95, 32.5, 123),
            new(92, 18.25, 90),
            new(90, 45, 150),
        };

        var recap = ChapterAnalyzer.BuildRecapFromBlackFrames(episodeId, frames, minimumRecapDuration: 5, maximumRecapBoundary: 120);

        Assert.NotNull(recap);
        Assert.Equal(episodeId, recap.EpisodeId);
        Assert.Equal(0, recap.Start);
        Assert.Equal(45, recap.End);
    }

    [Fact]
    public void BuildRecapFromBlackFrames_ReturnsLatestFrameBeforeIntroBoundary()
    {
        var episodeId = Guid.NewGuid();
        var frames = new List<BlackFrame>
        {
            new(95, 32.5, 123),
            new(92, 72, 250),
            new(90, 90, 300),
        };

        var recap = ChapterAnalyzer.BuildRecapFromBlackFrames(episodeId, frames, minimumRecapDuration: 5, maximumRecapBoundary: 80);

        Assert.NotNull(recap);
        Assert.Equal(72, recap.End);
    }

    [Fact]
    public void BuildRecapFromBlackFrames_ReturnsNull_WhenFrameBeforeMinimumDuration()
    {
        var frames = new List<BlackFrame> { new(90, 3.5, 20) };

        var recap = ChapterAnalyzer.BuildRecapFromBlackFrames(Guid.NewGuid(), frames, minimumRecapDuration: 5, maximumRecapBoundary: 120);

        Assert.Null(recap);
    }

    [Fact]
    public async Task DetectAdaptiveRecapBlackFrames_NormalizesThresholdFromFullDistributionScan()
    {
        var config = new PluginConfiguration
        {
            BlackFrameMinimumPercentage = 85,
            BlackFrameThreshold = 32,
            MinimumRecapDetectionDuration = 5,
            MaximumRecapDetectionDuration = 120,
        };
        var ffmpeg = new RecapBlackFrameFfmpeg(
        [
            new(2, 12, 5),
            new(5, 15, 8),
            new(50, 20, 10),
            new(95, 40, 20),
            new(88, 80, 30),
        ]);
        var analyzer = new ChapterAnalyzer(
            NullLogger<ChapterAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            config);
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 200, Path = "episode.mkv" };

        var blackFrames = await analyzer.DetectAdaptiveRecapBlackFramesAsync(episode, 120, CancellationToken.None);
        var recap = ChapterAnalyzer.BuildRecapFromBlackFrames(episode.EpisodeId, blackFrames, config.MinimumRecapDetectionDuration, 120);

        // Baseline (1st percentile) is 2, so the normalized minimum stays at the configured 85.
        Assert.Equal(new[] { 95, 88 }, blackFrames.Select(frame => frame.Percentage));
        Assert.NotNull(recap);
        Assert.Equal(80, recap.End);
        Assert.Equal(0, ffmpeg.LastMinimum);
        Assert.Equal(32, ffmpeg.LastThreshold);
        Assert.Equal(AnalysisMode.Recap, ffmpeg.LastMode);
        Assert.Equal(0, ffmpeg.LastRange?.Start);
        Assert.Equal(120, ffmpeg.LastRange?.End);
    }

    [Fact]
    public async Task DetectAdaptiveRecapBlackFrames_BrightContent_KeepsConfiguredMinimum()
    {
        // An 87% fade in normal (bright) content must satisfy the default 85% minimum;
        // the adaptive floor may only tighten the threshold for globally dark content.
        var config = new PluginConfiguration { MinimumRecapDetectionDuration = 5 };
        Assert.Equal(85, config.BlackFrameMinimumPercentage);

        BlackFrame[] scan = [.. Enumerable.Range(0, 40)
            .Select(i => new BlackFrame(i % 4, i * 0.5, i))
            .Append(new BlackFrame(87, 42.0, 1008))];
        var analyzer = new ChapterAnalyzer(
            NullLogger<ChapterAnalyzer>.Instance,
            new RecapBlackFrameFfmpeg(scan),
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            config);
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 1200, Path = "episode.mkv" };

        var blackFrames = await analyzer.DetectAdaptiveRecapBlackFramesAsync(episode, 120, CancellationToken.None);
        var recap = ChapterAnalyzer.BuildRecapFromBlackFrames(episode.EpisodeId, blackFrames, config.MinimumRecapDetectionDuration, 120);

        var frame = Assert.Single(blackFrames);
        Assert.Equal(87, frame.Percentage);
        Assert.NotNull(recap);
        Assert.Equal(42.0, recap.End);
    }

    [Fact]
    public async Task DetectAdaptiveRecapBlackFrames_DarkContent_RaisesThreshold()
    {
        // The same 87% fade inside globally dark content (baseline 45% black) must be
        // rejected: the floor caps at 30, lifting the normalized minimum to 89.
        var config = new PluginConfiguration { MinimumRecapDetectionDuration = 5 };

        BlackFrame[] scan = [.. Enumerable.Range(0, 100)
            .Select(i => new BlackFrame(45, i * 0.5, i))
            .Append(new BlackFrame(87, 42.0, 1008))];
        var analyzer = new ChapterAnalyzer(
            NullLogger<ChapterAnalyzer>.Instance,
            new RecapBlackFrameFfmpeg(scan),
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            config);
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 1200, Path = "episode.mkv" };

        var blackFrames = await analyzer.DetectAdaptiveRecapBlackFramesAsync(episode, 120, CancellationToken.None);

        Assert.Empty(blackFrames);
        Assert.Null(ChapterAnalyzer.BuildRecapFromBlackFrames(episode.EpisodeId, blackFrames, config.MinimumRecapDetectionDuration, 120));
    }

    [Fact]
    public void NormalizeThreshold_MovesWithScanDistribution()
    {
        var bright = Enumerable.Range(0, 200).Select(i => new BlackFrame(i < 190 ? 2 : 95, i, i)).ToList();
        var dark = Enumerable.Range(0, 200).Select(i => new BlackFrame(i < 190 ? 45 : 95, i, i)).ToList();

        var (brightMinimum, _) = BlackFrameThresholdHelper.NormalizeThreshold(bright, 85);
        var (darkMinimum, _) = BlackFrameThresholdHelper.NormalizeThreshold(dark, 85);

        Assert.Equal(85, brightMinimum);
        Assert.Equal(89, darkMinimum);
    }

    [Theory]
    [InlineData("Preview")]
    [InlineData("Trailer")]
    public void TestPreviewExpression(string chapterName)
    {
        var previewChapter = FindChapter(chapterName, AnalysisMode.Preview);

        Assert.NotNull(previewChapter);
        Assert.Equal(1890, previewChapter.Start);
        Assert.Equal(2000, previewChapter.End);
    }

    [Theory]
    [InlineData("Recap")]
    [InlineData("Previously")]
    public void TestRecapExpression(string chapterName)
    {
        var recapChapter = FindChapter(chapterName, AnalysisMode.Recap);

        Assert.NotNull(recapChapter);
        Assert.Equal(60, recapChapter.Start);
        Assert.Equal(90, recapChapter.End);
    }

    [Theory]
    [InlineData("Ad")]
    [InlineData("Advertisement")]
    [InlineData("Commercial")]
    [InlineData("Intermission")]
    public void TestCommercialExpression(string chapterName)
    {
        var commercialChapter = FindChapter(chapterName, AnalysisMode.Commercial);

        Assert.NotNull(commercialChapter);
        Assert.Equal(60, commercialChapter.Start);
        Assert.Equal(90, commercialChapter.End);
    }

    [Theory]
    [InlineData("Intermission/Intro")]
    [InlineData("Intermission/Intro Animation")]
    [InlineData("Commercial End")]
    [InlineData("Intermission End")]
    [InlineData("Intermission/Intro End")]
    [InlineData("Intermission/Intro Animation End")]
    public void TestCommercialExpressionIgnoresSponsorBlockOnlyAndEndLabels(string chapterName)
    {
        var commercialChapter = FindChapter(chapterName, AnalysisMode.Commercial);

        Assert.Null(commercialChapter);
    }

    [Theory]
    [InlineData("Intro: End", AnalysisMode.Introduction)]
    [InlineData("Credits: End", AnalysisMode.Credits)]
    [InlineData("Preview: End", AnalysisMode.Preview)]
    [InlineData("Recap: End", AnalysisMode.Recap)]
    [InlineData("Commercial: End", AnalysisMode.Commercial)]
    [InlineData("Intermission: End", AnalysisMode.Commercial)]
    public void TestChapterExpressionIgnoresColonDelimitedEndLabels(string chapterName, AnalysisMode mode)
    {
        var chapter = FindChapter(chapterName, mode);

        Assert.Null(chapter);
    }

    [Theory]
    [InlineData("[SponsorBlock]: Intro", AnalysisMode.Introduction)]
    [InlineData("[SponsorBlock]: intro", AnalysisMode.Introduction)]
    [InlineData("[SponsorBlock]: Endcards/Credits", AnalysisMode.Credits)]
    [InlineData("[SponsorBlock]: Outro", AnalysisMode.Credits)]
    [InlineData("[SponsorBlock]: outro", AnalysisMode.Credits)]
    [InlineData("[SponsorBlock]: Preview", AnalysisMode.Preview)]
    [InlineData("[SponsorBlock]: preview", AnalysisMode.Preview)]
    [InlineData("[SponsorBlock]: Recap", AnalysisMode.Recap)]
    [InlineData("[SponsorBlock]: Sponsor", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: sponsor", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Unpaid/Self Promotion", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Self Promotion", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: selfpromo", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Interaction Reminder (Subscribe)", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: interaction", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Tangents/Jokes", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Filler", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: filler", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Music: Non-Music Section", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Non-Music Section", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: music_offtopic", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Intermission", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Intermission/Intro Animation", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Preview/Recap", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Preview/Recap/Hook", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: Hook/Greetings", AnalysisMode.Commercial)]
    [InlineData("[SponsorBlock]: hook", AnalysisMode.Commercial)]
    public void TestSponsorBlockChapterLabelsMapToExpectedMode(string chapterName, AnalysisMode expectedMode)
    {
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            var chapter = FindChapter(chapterName, mode, expressionOverride: string.Empty);

            if (mode == expectedMode)
            {
                Assert.NotNull(chapter);
            }
            else
            {
                Assert.Null(chapter);
            }
        }
    }

    [Fact]
    public void TestSponsorBlockDetectionCanBeDisabled()
    {
        var commercialChapter = FindChapter(
            "[SponsorBlock]: Intermission/Intro Animation",
            AnalysisMode.Commercial,
            expressionOverride: string.Empty,
            enableSponsorBlockChapterDetection: false);

        Assert.Null(commercialChapter);
    }

    [Fact]
    public void TestUnmappedSponsorBlockChapterFallsBackToUserRegex()
    {
        var introChapter = FindChapter(
            "[SponsorBlock]: Custom Intro",
            AnalysisMode.Introduction,
            expressionOverride: "Custom Intro");

        Assert.NotNull(introChapter);
        Assert.Equal(60, introChapter.Start);
        Assert.Equal(90, introChapter.End);
    }

    [Fact]
    public void PluginGetChapters_ReturnsEmptyList_WhenChapterManagerReturnsNull()
    {
        using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
        {
            var plugin = Plugin.Instance!;
            EntrypointTestHelpers.SetPrivateField(plugin, "_chapterRepository", NullChapterManager.Create());

            var chapters = plugin.GetChapters(Guid.NewGuid());

            Assert.Empty(chapters);
        }
    }

    private Segment? FindChapter(
        string chapterName,
        AnalysisMode mode,
        string? expressionOverride = null,
        bool enableSponsorBlockChapterDetection = true)
    {
        var analyzer = new ChapterAnalyzer(NullLogger<ChapterAnalyzer>.Instance, null!, DatabaseTestHelpers.CreateTempSegmentDatabase());
        var chapters = CreateChapters(chapterName, mode);

        var config = new Configuration.PluginConfiguration();
        var expression = mode switch
        {
            AnalysisMode.Introduction => config.ChapterAnalyzerIntroductionPattern,
            AnalysisMode.Credits => config.ChapterAnalyzerEndCreditsPattern,
            AnalysisMode.Preview => config.ChapterAnalyzerPreviewPattern,
            AnalysisMode.Recap => config.ChapterAnalyzerRecapPattern,
            AnalysisMode.Commercial => config.ChapterAnalyzerCommercialPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        return analyzer.FindMatchingChapter(
            new() { Duration = 2000 },
            chapters,
            expressionOverride ?? expression,
            mode,
            enableSponsorBlockChapterDetection);
    }

    private Collection<ChapterInfo> CreateChapters(string name, AnalysisMode mode)
    {
        var earlyName = mode is AnalysisMode.Introduction or AnalysisMode.Recap or AnalysisMode.Commercial ? name : "Introduction";
        var lateName = mode is AnalysisMode.Credits or AnalysisMode.Preview ? name : "Credits";
        var chapters = new[]
        {
            CreateChapter("Cold Open", 0),
            CreateChapter(earlyName, 60),
            CreateChapter("Main Episode", 90),
            CreateChapter(lateName, 1890)
        };

        return new(new List<ChapterInfo>(chapters));
    }

    /// <summary>
    /// Create a ChapterInfo object.
    /// </summary>
    /// <param name="name">Chapter name.</param>
    /// <param name="position">Chapter position (in seconds).</param>
    /// <returns>ChapterInfo.</returns>
    private static ChapterInfo CreateChapter(string name, int position)
    {
        return new()
        {
            Name = name,
            StartPositionTicks = TimeSpan.FromSeconds(position).Ticks
        };
    }

    private sealed class RecapBlackFrameFfmpeg(BlackFrame[] frames) : IFFmpegService
    {
        public TimeRange? LastRange { get; private set; }

        public int? LastMinimum { get; private set; }

        public int? LastThreshold { get; private set; }

        public AnalysisMode? LastMode { get; private set; }

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
            LastRange = range;
            LastMinimum = minimum;
            LastThreshold = threshold;
            LastMode = mode;
            return Task.FromResult(frames);
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

        public FFmpegCheckResult GetCheckResult() => FFmpegCheckResult.NotRun;
    }

    private class NullChapterManager : DispatchProxy
    {
        public static IChapterManager Create()
            => DispatchProxy.Create<IChapterManager, NullChapterManager>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IChapterManager.GetChapters))
            {
                return null;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }
}
