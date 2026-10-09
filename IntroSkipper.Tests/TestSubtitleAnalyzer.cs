// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
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
    public async Task RecapSubtitle_AfterIntroUsesLaterChapterAsEnd()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.SeedUserSegmentAsync(episodeId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(0), DatabaseTestHelpers.Ticks(20));
        var config = new PluginConfiguration
        {
            EnableSubtitleRecapDetection = true,
            MinimumRecapDuration = 5,
            MaximumRecapDuration = 120,
        };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(
            config,
            [new ChapterInfo { StartPositionTicks = DatabaseTestHelpers.Ticks(40) }]);
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => [new SubtitleCue(30, 33, "Previously on the story")],
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 120, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Recap, CancellationToken.None);

        var recap = Assert.Single(await database.GetSegmentsAsync(episodeId), segment => segment.Type == AnalysisMode.Recap);
        Assert.Equal(30, TickConversions.ToSeconds(recap.StartTicks));
        Assert.Equal(40, TickConversions.ToSeconds(recap.EndTicks));
        Assert.Equal(SegmentSource.Subtitle, recap.Source);
    }

    [Fact]
    public async Task RecapSubtitle_AfterIntroWithoutChapterLogsDetectionAndDoesNotWrite()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.SeedUserSegmentAsync(episodeId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(20), DatabaseTestHelpers.Ticks(30));
        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Recap,
            [new Segment(episodeId, new TimeRange(5, 15))],
            SegmentSource.Subtitle,
            configHash: "prior-subtitle-config");
        var config = new PluginConfiguration
        {
            EnableSubtitleRecapDetection = true,
            MinimumRecapDuration = 5,
            MaximumRecapDuration = 120,
        };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config, Array.Empty<ChapterInfo>());
        var logger = new RecordingLogger();
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => [new SubtitleCue(30, 33, "Previously on the story")],
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 120, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(logger, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Recap, CancellationToken.None);

        var recap = Assert.Single(await database.GetSegmentsAsync(episodeId), segment => segment.Type == AnalysisMode.Recap);
        Assert.Equal(5, TickConversions.ToSeconds(recap.StartTicks));
        Assert.Equal(15, TickConversions.ToSeconds(recap.EndTicks));
        Assert.NotEqual(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Recap));
        Assert.True(episode.HasUnresolvedSubtitleDetection(AnalysisMode.Recap));
        Assert.Contains(logger.Messages, message => message.Contains("subtitle recap detected at 30.00s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewSubtitle_IsIndependentOfCreditsAndRunsToEpisodeEnd()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Preview,
            [new Segment(episodeId, new TimeRange(140, 170))],
            SegmentSource.CreditsDerived,
            configHash: "credits");
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
        Assert.NotEqual(SegmentSource.CreditsDerived, preview.Source);
        Assert.Equal(100, TickConversions.ToSeconds(preview.StartTicks));
        Assert.Equal(180, TickConversions.ToSeconds(preview.EndTicks));
        Assert.Equal(SegmentSource.Subtitle, preview.Source);
    }

    [Fact]
    public async Task DefaultPreviewPattern_MatchesNowThePreview()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration { EnableSubtitlePreviewDetection = true, MinimumPreviewDuration = 5 };
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => [new SubtitleCue(100, 103, "Now the preview")],
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episodeId));
        Assert.Equal(100, TickConversions.ToSeconds(preview.StartTicks));
        Assert.Equal(180, TickConversions.ToSeconds(preview.EndTicks));
        Assert.Equal(SegmentSource.Subtitle, preview.Source);
    }

    [Fact]
    public async Task RejectedPreviewSubtitle_PreservesCreditsDerivedPreview()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Preview,
            [new Segment(episodeId, new TimeRange(100, 110))],
            SegmentSource.Chapter);
        var blocker = Assert.Single(await database.GetSegmentsAsync(episodeId));
        await database.DeleteSegmentAsync(episodeId, blocker.Id);
        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Preview,
            [new Segment(episodeId, new TimeRange(140, 180))],
            SegmentSource.CreditsDerived,
            configHash: "credits");

        var config = new PluginConfiguration
        {
            EnableSubtitlePreviewDetection = true,
            MinimumPreviewDuration = 5,
        };
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => [new SubtitleCue(100, 103, "Here's the preview")],
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episodeId));
        Assert.Equal(SegmentSource.CreditsDerived, preview.Source);
        Assert.NotEqual(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
        Assert.True(episode.HasRejectedSubtitleCandidate(AnalysisMode.Preview));
        Assert.False(episode.HasUnresolvedSubtitleDetection(AnalysisMode.Preview));
    }

    [Fact]
    public async Task UnmatchedPreviewSubtitle_PreservesCreditsDerivedFallback()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Preview,
            [new Segment(episodeId, new TimeRange(140, 180))],
            SegmentSource.CreditsDerived,
            configHash: "credits");

        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Preview,
            [new Segment(episodeId, new TimeRange(100, 180))],
            SegmentSource.Subtitle,
            configHash: "old-subtitle-config");

        var config = new PluginConfiguration { EnableSubtitlePreviewDetection = true };
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => [new SubtitleCue(100, 103, "A normal line")],
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv", AnalysisConfigHash = "subtitle" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episodeId));
        Assert.Equal(SegmentSource.CreditsDerived, preview.Source);
        Assert.NotEqual(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public async Task IncompleteSubtitleScanWithoutMatch_PreservesSubtitleRowAndRemainsRetryable()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episodeId,
            AnalysisMode.Preview,
            [new Segment(episodeId, new TimeRange(100, 180))],
            SegmentSource.Subtitle,
            configHash: "previous-config");
        var config = new PluginConfiguration { EnableSubtitlePreviewDetection = true };
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => throw new SubtitleExtractionException(
                "One subtitle source failed.",
                [new SubtitleCue(100, 103, "Ordinary dialogue")],
                new IOException("source failed")),
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv", AnalysisConfigHash = "new-config" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episodeId));
        Assert.Equal("previous-config", preview.ConfigHash);
        Assert.True(episode.HasUnresolvedSubtitleDetection(AnalysisMode.Preview));
        Assert.NotEqual(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public async Task IncompleteSubtitleScanWithMatch_WritesCandidateAndRemainsRetryable()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration { EnableSubtitlePreviewDetection = true };
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => throw new SubtitleExtractionException(
                "One subtitle source failed.",
                [new SubtitleCue(100, 103, "Here's the preview")],
                new IOException("source failed")),
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv", AnalysisConfigHash = "new-config" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episodeId));
        Assert.Equal(SegmentSource.Subtitle, preview.Source);
        Assert.Equal(100, TickConversions.ToSeconds(preview.StartTicks));
        Assert.Equal(180, TickConversions.ToSeconds(preview.EndTicks));
        Assert.True(episode.HasUnresolvedSubtitleDetection(AnalysisMode.Preview));
    }

    [Fact]
    public async Task PreviewSubtitle_RejectsCandidateLongerThanMaximum()
    {
        var episodeId = Guid.NewGuid();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration
        {
            EnableSubtitlePreviewDetection = true,
            MinimumPreviewDuration = 5,
            MaximumPreviewDuration = 60,
        };
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ => [new SubtitleCue(100, 103, "Here's the preview")],
        };
        var episode = new QueuedEpisode { EpisodeId = episodeId, Duration = 180, Path = "episode.mkv" };
        var analyzer = new SubtitleAnalyzer(NullLogger<SubtitleAnalyzer>.Instance, ffmpeg, database, config);

        await analyzer.AnalyzeMediaFiles([episode], AnalysisMode.Preview, CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(episodeId));
        Assert.NotEqual(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public async Task DisabledSubtitleModeDoesNotReadSubtitles()
    {
        var config = new PluginConfiguration();
        var extractionCalled = false;
        var ffmpeg = new StubFFmpegService
        {
            SubtitleCues = _ =>
            {
                extractionCalled = true;
                return [];
            },
        };
        var analyzer = new SubtitleAnalyzer(
            NullLogger<SubtitleAnalyzer>.Instance,
            ffmpeg,
            DatabaseTestHelpers.CreateTempSegmentDatabase(),
            config);

        await analyzer.AnalyzeMediaFiles(
            [new QueuedEpisode { EpisodeId = Guid.NewGuid() }],
            AnalysisMode.Preview,
            CancellationToken.None);

        Assert.False(extractionCalled);
    }

    private sealed class RecordingLogger : ILogger<SubtitleAnalyzer>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
