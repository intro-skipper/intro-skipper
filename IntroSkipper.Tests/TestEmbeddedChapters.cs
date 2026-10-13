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
                { "start_time": "12.500", "tags": { "title": "Opening" } },
                { "start_time": 98.25, "tags": { "title": "Main" } }
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
    public async Task ChapterAnalyzer_IgnoresJellyfinDatabaseChaptersWhenMediaHasNoEmbeddedChapters()
    {
        var config = new PluginConfiguration
        {
            EnableSponsorBlockChapterDetection = false,
            UseEmbeddedChaptersForAnalysis = true,
        };
        using var scope = EntrypointTestHelpers.CreatePluginScope(
            config,
            [new ChapterInfo { Name = "Opening", StartPositionTicks = DatabaseTestHelpers.Ticks(60) }]);
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
}
