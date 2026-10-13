// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TestEmbeddedChapters
{
    [Fact]
    public void ParseChapters_ReadsEmbeddedChapterStartsAndTitles()
    {
        var chapters = FFmpegOutputParser.ParseChapters("""
            {
              "chapters": [
                { "start_time": 98.25, "tags": { "title": "Main" } },
                { "start_time": "12.500", "tags": { "title": "Opening" } }
              ]
            }
            """);

        Assert.Collection(
            chapters,
            chapter =>
            {
                Assert.Equal(DatabaseTestHelpers.Ticks(12.5), chapter.StartPositionTicks);
                Assert.Equal("Opening", chapter.Name);
            },
            chapter =>
            {
                Assert.Equal(DatabaseTestHelpers.Ticks(98.25), chapter.StartPositionTicks);
                Assert.Equal("Main", chapter.Name);
            });
    }

    [Fact]
    public void ParseChapters_SkipsNonFiniteAndOutOfRangeStarts()
    {
        var chapters = FFmpegOutputParser.ParseChapters("""
            {
              "chapters": [
                { "start_time": "NaN", "tags": { "title": "NaN" } },
                { "start_time": "1e100", "tags": { "title": "Too late" } },
                { "start_time": -1, "tags": { "title": "Before start" } },
                { "start_time": 15, "tags": { "title": "Opening" } }
              ]
            }
            """);

        var chapter = Assert.Single(chapters);
        Assert.Equal(DatabaseTestHelpers.Ticks(15), chapter.StartPositionTicks);
        Assert.Equal("Opening", chapter.Name);
    }

    [Fact]
    public async Task ChapterAnalyzer_IgnoresJellyfinDatabaseChaptersWhenMediaHasNoEmbeddedChapters()
    {
        var config = new PluginConfiguration
        {
            EnableSponsorBlockChapterDetection = false,
            UseEmbeddedChaptersForAnalysis = true,
        };
        using var scope = EntrypointTestHelpers.CreatePluginScope(
            config,
            [
                new ChapterInfo { Name = "Opening", StartPositionTicks = DatabaseTestHelpers.Ticks(60) },
                new ChapterInfo { Name = "Main", StartPositionTicks = DatabaseTestHelpers.Ticks(100) },
            ]);
        var ffmpeg = new StubFFmpegService { EmbeddedChapters = _ => [] };
        var analyzer = new ChapterAnalyzer(
            NullLogger<ChapterAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            config);
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Duration = 2000,
            Path = "episode.mkv",
        };

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Introduction, CancellationToken.None);

        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Fact]
    public async Task ChapterAnalyzer_MarksAnEmbeddedProbeFailureAsFailed()
    {
        var config = new PluginConfiguration
        {
            EnableSponsorBlockChapterDetection = false,
            UseEmbeddedChaptersForAnalysis = true,
        };
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var ffmpeg = new StubFFmpegService
        {
            EmbeddedChapters = _ => throw new InvalidOperationException("probe failed"),
        };
        var analyzer = new ChapterAnalyzer(
            NullLogger<ChapterAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            config);
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            Duration = 2000,
            Path = "episode.mkv",
        };

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Introduction, CancellationToken.None);

        Assert.Equal(EpisodeState.AnalysisFailed, episode.GetAnalyzed(AnalysisMode.Introduction));
    }
}
