// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
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
    public void ParseChapters_ReadsStartsAndTitlesInStartOrder()
    {
        var chapters = FFmpegOutputParser.ParseChapters("""
            {
              "chapters": [
                { "start_time": "98.250000", "tags": { "title": "Main" } },
                { "start_time": "90.048625", "tags": { "title": "Opening" } },
                { "start_time": "0.000000" }
              ]
            }
            """);

        Assert.Equal(
            [(0L, string.Empty), (900_486_250L, "Opening"), (982_500_000L, "Main")],
            chapters.Select(chapter => (chapter.StartPositionTicks, chapter.Name)));
    }

    [Fact]
    public void ParseChapters_DropsStartsThatAreNotTickCounts()
    {
        var chapters = FFmpegOutputParser.ParseChapters("""
            {
              "chapters": [
                { "start_time": "NaN", "tags": { "title": "NaN" } },
                { "start_time": "1e100", "tags": { "title": "Too late" } },
                { "start_time": "-1.000000", "tags": { "title": "Before start" } },
                { "start_time": "15.000000", "tags": { "title": "Opening" } }
              ]
            }
            """);

        var chapter = Assert.Single(chapters);
        Assert.Equal(150_000_000, chapter.StartPositionTicks);
        Assert.Equal("Opening", chapter.Name);
    }

    /// <summary>
    /// Pins the JSON shape the installed ffprobe prints for a Matroska file's chapters.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task GetEmbeddedChaptersAsync_ReadsTheContainerChapters()
    {
        using var directory = new TempDirectory();
        var metadata = directory.Join("chapters.txt");
        var path = directory.Join("chapters.mkv");
        await File.WriteAllTextAsync(
            metadata,
            """
            ;FFMETADATA1
            [CHAPTER]
            TIMEBASE=1/1000000
            START=0
            END=1048625
            title=Opening
            [CHAPTER]
            TIMEBASE=1/1000000
            START=1048625
            END=2000000
            title=Main
            """.ReplaceLineEndings("\n"));
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=32x32:r=25:d=2", "-f", "ffmetadata", "-i", metadata, "-map", "0", "-map_chapters", "1", "-c:v", "ffv1", path]);

        var chapters = await FfmpegTestHelpers.CreateFFmpegService().GetEmbeddedChaptersAsync(
            new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "chapters", Path = path });

        Assert.Equal(
            [(0L, "Opening"), (10_486_250L, "Main")],
            chapters.Select(chapter => (chapter.StartPositionTicks, chapter.Name)));
    }

    [Theory]
    [InlineData(false, EpisodeState.NotAnalyzed)]
    [InlineData(true, EpisodeState.AnalysisFailed)]
    public async Task ChapterAnalyzer_UsesOnlyEmbeddedChaptersAndPropagatesProbeFailures(bool probeFails, EpisodeState expectedState)
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
        var ffmpeg = new StubFFmpegService
        {
            EmbeddedChapters = _ => probeFails
                ? throw new InvalidOperationException("probe failed")
                : [],
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

        Assert.Equal(expectedState, episode.GetAnalyzed(AnalysisMode.Introduction));
    }
}
