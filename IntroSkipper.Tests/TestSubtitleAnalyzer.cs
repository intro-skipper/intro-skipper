// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestSubtitleAnalyzer
{
    [Fact]
    public void ParseWebVtt_ReadsCueTimesAndCombinesLines()
    {
        var cues = FFmpegOutputParser.ParseWebVtt("""
            WEBVTT

            00:00:10.500 --> 00:00:12.000
            [All Might]
            Previously on...

            00:01:00.000 --> 00:01:02.000
            Here's the preview
            """);

        Assert.Equal(2, cues.Length);
        Assert.Equal(10.5, cues[0].Start);
        Assert.Equal("[All Might] Previously on...", cues[0].Text);
        Assert.Equal(60, cues[1].Start);
    }

    [Fact]
    public async Task RecapSubtitle_EndsAtTheDetectedIntroStart()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.SeedUserSegmentAsync(episodeId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(60), DatabaseTestHelpers.Ticks(90));

        var config = new PluginConfiguration
        {
            EnableSubtitleRecapDetection = true,
            MinimumRecapDuration = 5,
            MaximumRecapDuration = 120,
        };
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => [new SubtitleCue(10, 13, "[Narrator] Previously on the story")],
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 120, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Recap, CancellationToken.None);

        var rows = await database.GetSegmentsAsync(episodeId);
        Assert.Single(rows, segment => segment.Type == AnalysisMode.Introduction);

        var recap = Assert.Single(rows, segment => segment.Type == AnalysisMode.Recap);
        Assert.Equal(10, TickConversions.ToSeconds(recap.StartTicks));
        Assert.Equal(60, TickConversions.ToSeconds(recap.EndTicks));
        Assert.Equal(SegmentSource.Subtitle, recap.Source);
    }

    [Fact]
    public async Task PreviewSubtitle_IsIndependentOfCreditsAndRunsToEpisodeEnd()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration
        {
            EnableSubtitlePreviewDetection = true,
            MinimumPreviewDuration = 5,
        };
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => [new SubtitleCue(100, 103, "[Name] here's the preview")],
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episodeId));
        Assert.Equal(AnalysisMode.Preview, preview.Type);
        Assert.Equal(100, TickConversions.ToSeconds(preview.StartTicks));
        Assert.Equal(180, TickConversions.ToSeconds(preview.EndTicks));
        Assert.Equal(SegmentSource.Subtitle, preview.Source);
    }

    [Fact]
    public async Task DisabledSubtitleModeDoesNotReadSubtitles()
    {
        var config = new PluginConfiguration();
        var analyzer = new SubtitleAnalyzer(
            NullLogger<SubtitleAnalyzer>.Instance,
            new StubFFmpegService(),
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            config);

        await analyzer.AnalyzeMediaFiles(
            [new QueuedEpisode { EpisodeId = Guid.NewGuid() }],
            AnalysisMode.Preview,
            CancellationToken.None);
    }
}
