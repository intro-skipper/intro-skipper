// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Providers;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestKeyframeAnalyzerFallback
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DetectCreditsAsync_AdditionalScenesRequireVisualEvidence(bool withVisuals, bool earlierSceneHasVisuals)
    {
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Duration = 1000,
            CreditsFingerprintStart = 550,
            CreditsFingerprintEnd = 1000,
        };
        KeyframePage[] pages = [.. Enumerable.Range(0, 225).Select(index =>
        {
            var time = index * 2.0;
            var earlierScene = time >= 150 && time <= 210;
            var finalScene = time >= 400;
            var visual = !withVisuals || (earlierScene && !earlierSceneHasVisuals)
                ? null
                : earlierScene || finalScene
                    ? KeyframeVisuals.Black(time)
                    : KeyframeVisuals.Content(time);
            return new KeyframePage(new BlackFrame(earlierScene || finalScene ? 95 : 0, time, index), visual);
        })];
        var ffmpeg = new StubFFmpegService
        {
            KeyframeScan = (_, _) => pages,
            RangeBlackFrames = (_, _, _, _, _) => [],
            BlackIntervals = (_, _, _, _) => [],
            LumaWindows = (_, _, _) => null,
        };
        var config = new PluginConfiguration();
        var analyzer = new KeyframeAnalyzer(
            NullLogger<KeyframeAnalyzer>.Instance, ffmpeg, DatabaseTestHelpers.CreateTempCacheService(), config);

        var candidates = await analyzer.DetectCreditsAsync(episode, CancellationToken.None);
        var combined = CreditsCandidateCombiner.Combine(candidates, episode.Duration, config.MinimumCreditsDuration);

        var expected = withVisuals && earlierSceneHasVisuals
            ? new[] { (700.0, 760.0), (950.0, 1000.0) }
            : [(950.0, 1000.0)];
        Assert.Equal(expected, combined.Select(candidate => (candidate.Segment.Start, candidate.Segment.End)));

        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Credits, combined);
        var mirrored = await new SegmentDtoFactory(database).CreateAsync(episode.EpisodeId, CancellationToken.None);

        Assert.All(mirrored, segment => Assert.Equal(MediaSegmentType.Outro, segment.Type));
        Assert.Equal(expected.Select(range => (TickConversions.FromSeconds(range.Item1), TickConversions.FromSeconds(range.Item2))),
            mirrored.Select(segment => (segment.StartTicks, segment.EndTicks)));
    }

    [Fact]
    public async Task DetectCreditsAsync_WithoutVisualsFallsBackToLatestSceneMeetingRefinedMinimum()
    {
        KeyframePage[] pages = [.. Enumerable.Range(0, 43).Select(index =>
        {
            var time = index * 2.0;
            var black = (time >= 10 && time <= 30) || time >= 70;
            return new KeyframePage(new BlackFrame(black ? 95 : 0, time, index), null);
        })];
        var ffmpeg = new StubFFmpegService
        {
            KeyframeScan = (_, _) => pages,
            RangeBlackFrames = (_, _, _, _, _) => [],
            BlackIntervals = (_, _, _, _) => [],
            LumaWindows = (_, _, _) => null,
        };
        var analyzer = new KeyframeAnalyzer(
            NullLogger<KeyframeAnalyzer>.Instance, ffmpeg, DatabaseTestHelpers.CreateTempCacheService(), new PluginConfiguration());
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 100, CreditsFingerprintEnd = 100 };

        var candidates = await analyzer.DetectCreditsAsync(episode, CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(SegmentSource.BlackFrame, candidate.Source);
        Assert.Equal((10.0, 30.0), (candidate.Segment.Start, candidate.Segment.End));
    }
}
