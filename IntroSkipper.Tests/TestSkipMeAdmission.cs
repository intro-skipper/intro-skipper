// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestSkipMeAdmission
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoritativeCreditsBypassIntroOverlapWithoutChangingTheIntro(bool manualIntro)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        using var db = new TempSegmentDb();
        var episode = Episode();
        if (manualIntro)
        {
            await db.Database.SeedUserSegmentAsync(episode.EpisodeId, AnalysisMode.Introduction, Ticks(700), Ticks(950));
        }
        else
        {
            await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Introduction,
                [new Segment(episode.EpisodeId, new TimeRange(700, 950))], SegmentSource.Chapter);
        }

        var intro = Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId));
        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration,
            [new MediaSegmentDto { ItemId = episode.EpisodeId, Type = MediaSegmentType.Outro, StartTicks = Ticks(850.123), EndTicks = Ticks(900.456) }]);
        var task = new BaseItemAnalyzerTask(NullLogger.Instance, NullLoggerFactory.Instance, null!, new StubFFmpegService(), null!, db.Database);

        await task.AnalyzeItemsAsync([episode], AnalysisMode.Credits, AnalyzerAction.Default, true, CancellationToken.None);

        var rows = await db.Database.GetSegmentsAsync(episode.EpisodeId);
        var credits = Assert.Single(rows, row => row.Type == AnalysisMode.Credits);
        Assert.Equal(SegmentSource.SkipMe, credits.Source);
        Assert.Equal(Ticks(850.123), credits.StartTicks);
        Assert.Equal(Ticks(900.456), credits.EndTicks);
        var retainedIntro = Assert.Single(rows, row => row.Type == AnalysisMode.Introduction);
        Assert.Equal((intro.Id, intro.Source, intro.StartTicks, intro.EndTicks), (retainedIntro.Id, retainedIntro.Source, retainedIntro.StartTicks, retainedIntro.EndTicks));
    }

    [Theory]
    [InlineData(SegmentSource.Chapter)]
    [InlineData(SegmentSource.Chromaprint)]
    [InlineData(SegmentSource.BlackFrame)]
    [InlineData(SegmentSource.Combined)]
    public async Task LocalCreditsStillRespectIntroOverlap(SegmentSource source)
    {
        using var db = new TempSegmentDb();
        var episode = Episode();
        await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Introduction,
            [new Segment(episode.EpisodeId, new TimeRange(700, 950))], SegmentSource.Chapter);

        var written = await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Credits,
            [new Segment(episode.EpisodeId, new TimeRange(850, 900))], source);

        Assert.Equal(0, written);
        Assert.Equal(AnalysisMode.Introduction, Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId)).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthoritativeCreditsStillRespectManualCreditsAndTombstones(bool manualCredits)
    {
        using var db = new TempSegmentDb();
        var episode = Episode();
        if (manualCredits)
        {
            await db.Database.SeedUserSegmentAsync(episode.EpisodeId, AnalysisMode.Credits, Ticks(850), Ticks(900));
        }
        else
        {
            await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Credits,
                [new Segment(episode.EpisodeId, new TimeRange(850, 900))], SegmentSource.SkipMe);
            var row = Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId));
            await db.Database.DeleteSegmentAsync(episode.EpisodeId, row.Id);
        }

        var written = await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Credits,
            [new Segment(episode.EpisodeId, new TimeRange(800, 950))], SegmentSource.SkipMe);

        Assert.Equal(0, written);
        var retained = Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId, includeSuppressed: true));
        Assert.Equal(Ticks(850), retained.StartTicks);
        Assert.Equal(Ticks(900), retained.EndTicks);
        Assert.Equal(manualCredits ? SegmentState.Active : SegmentState.Suppressed, retained.State);
    }

    [Theory]
    [InlineData(null, EpisodeState.NoSegments)]
    [InlineData(AnalysisMode.Introduction, EpisodeState.Analyzed)]
    [InlineData(AnalysisMode.Preview, EpisodeState.NoSegments)]
    public async Task RejectedMatchUsesTheRemainingActiveStateForItsMode(AnalysisMode? retainedMode, EpisodeState expected)
    {
        var config = new PluginConfiguration();
        using var scope = EntrypointTestHelpers.CreatePluginScope(config);
        using var db = new TempSegmentDb();
        var episode = Episode();
        await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Introduction,
            [new Segment(episode.EpisodeId, new TimeRange(10, 60))], SegmentSource.SkipMe);
        var rejected = Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId));
        await db.Database.DeleteSegmentAsync(episode.EpisodeId, rejected.Id);
        if (retainedMode is { } mode)
        {
            await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, mode,
                [new Segment(episode.EpisodeId, new TimeRange(100, 160))], SegmentSource.Chapter);
        }

        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration,
            [new MediaSegmentDto { ItemId = episode.EpisodeId, Type = MediaSegmentType.Intro, StartTicks = Ticks(10), EndTicks = Ticks(60) }]);
        var task = new BaseItemAnalyzerTask(NullLogger.Instance, NullLoggerFactory.Instance, null!, new StubFFmpegService(), null!, db.Database);

        await task.AnalyzeItemsAsync([episode], AnalysisMode.Introduction, AnalyzerAction.Default, true, CancellationToken.None);

        Assert.Equal(expected, episode.GetAnalyzed(AnalysisMode.Introduction));
        var active = await db.Database.GetSegmentsAsync(episode.EpisodeId);
        Assert.Equal(retainedMode == AnalysisMode.Introduction, active.Any(row => row.Type == AnalysisMode.Introduction));
        episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NotAnalyzed);
        var snapshot = await db.Database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        new QueueVerifier(config, [AnalysisMode.Introduction], snapshot, true).Classify(episode);
        Assert.Equal(expected, episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    private static QueuedEpisode Episode() => new()
    {
        EpisodeId = Guid.NewGuid(),
        SeasonId = Guid.NewGuid(),
        Duration = 1000,
        SeasonNumber = 1,
    };

    private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;
}
