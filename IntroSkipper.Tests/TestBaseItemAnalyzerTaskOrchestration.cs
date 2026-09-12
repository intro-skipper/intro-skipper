// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only
namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestBaseItemAnalyzerTaskOrchestration
{
    [Fact]
    public async Task AnalyzeItemsAsync_PropagatesCancellationToFfmpegValidation()
    {
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var analyzer = new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            EntrypointTestHelpers.CreateSeasonResolver(EntrypointTestHelpers.CreateLibraryManager()),
            ffmpegService: FfmpegTestHelpers.CreateFFmpegService(),
            cacheService: null!,
            cacheDatabase: null!,
            database: null!);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeItemsAsync(new Progress<double>(), cancellation.Token));
    }

    /// <summary>
    /// Credits the user authored never enter the credits pass, so the Preview mode has to
    /// refresh the derived preview when the preview minimum changes: a stale one goes, an
    /// eligible one appears.
    /// </summary>
    [Theory]
    [InlineData(15, 30, false)]
    [InlineData(30, 15, true)]
    public async Task PreviewMode_RefreshesDerivedPreviewOverUserProvidedCredits(int before, int after, bool expectPreview)
    {
        var config = new PluginConfiguration
        {
            AnimePreviewFromCreditsEnd = true,
            MinimumPreviewDuration = before,
            ChapterAnalyzerPreviewPattern = string.Empty,
            EnableSponsorBlockChapterDetection = false,
        };
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var episode = new QueuedEpisode
        {
            EpisodeId = Guid.NewGuid(),
            SeasonId = Guid.NewGuid(),
            SeriesId = Guid.NewGuid(),
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Category = QueuedMediaCategory.AnimeEpisode,
            Name = "Episode 1",
            Path = "/media/episode-1.mkv",
            Duration = 1320,
            CreditsFingerprintStart = 870,
            CreditsFingerprintEnd = 1320,
        };
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.SeedUserSegmentAsync(episode.EpisodeId, AnalysisMode.Credits, TimeSpan.FromSeconds(900).Ticks, TimeSpan.FromSeconds(1300).Ticks);
        await AnimePreviewDeriver.DeriveAsync(database, [episode], before, CancellationToken.None);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [episode.EpisodeId], ConfigHasher.Analysis(config, AnalysisMode.Credits, AnalyzerAction.Default, false));
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Preview, [episode.EpisodeId], ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, false));

        // A fresh scan classifies from stored state, not from the seeding run's in-memory state.
        config.MinimumPreviewDuration = after;
        episode.SetAnalyzed(AnalysisMode.Credits, EpisodeState.NotAnalyzed);
        episode.SetAnalyzed(AnalysisMode.Preview, EpisodeState.NotAnalyzed);
        var snapshot = await database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        new QueueVerifier(config, [AnalysisMode.Credits, AnalysisMode.Preview], snapshot, false).Classify(episode);
        Assert.Equal(EpisodeState.UserProvided, episode.GetAnalyzed(AnalysisMode.Credits));
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Preview));

        var task = new BaseItemAnalyzerTask(NullLoggerFactory.Instance, null!, new StubFFmpegService(), DatabaseTestHelpers.CreateTempCacheService(), null!, database);
        await task.AnalyzeItemsAsync([episode], AnalysisMode.Credits, AnalyzerAction.Default, false, CancellationToken.None);
        await task.AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        var rows = await database.GetSegmentsAsync(episode.EpisodeId);
        Assert.Equal(SegmentSource.User, Assert.Single(rows, s => s.Type == AnalysisMode.Credits).Source);
        Assert.Equal(expectPreview, rows.Any(s => s.Type == AnalysisMode.Preview));
    }

    [Fact]
    public async Task AnalyzeItemsAsync_InvalidFfmpeg_IsProbedOnceAcrossAnalyzerAndQueueVerification()
    {
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var episode = JellyfinItems.Episode(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), path: "/media/missing-episode.mkv");
        var libraryManager = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], JellyfinItems.WithParents(episode));
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", libraryManager);
        var ffmpegService = new StubFFmpegService { VersionCheck = () => false };
        var analyzer = new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            EntrypointTestHelpers.CreateSeasonResolver(libraryManager),
            ffmpegService,
            cacheService: null!,
            cacheDatabase: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        await analyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(1, ffmpegService.VersionCheckCalls);
    }

    /// <summary>
    /// The watcher queues changed items by their own id and the run analyzes the season
    /// holding each one. A scan hands over the season it resolved itself.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzeItemsAsync_ScopedToAnEpisodeIdOrAResolvedSeason_AnalyzesThatSeason(bool byEpisodeId)
    {
        var config = new PluginConfiguration
        {
            ScanIntroduction = true,
            ScanCredits = false,
            ScanRecap = false,
            ScanPreview = false,
            ScanCommercial = false,
        };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config);
        var mediaPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".mkv");
        await File.WriteAllTextAsync(mediaPath, string.Empty);
        try
        {
            var seasonId = Guid.NewGuid();
            var episode = JellyfinItems.Episode(Guid.NewGuid(), Guid.NewGuid(), seasonId, path: mediaPath);
            var libraryManager = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], JellyfinItems.WithParents(episode));
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", libraryManager);
            var database = DatabaseTestHelpers.CreateTempSegmentDatabase();

            // A season whose analyzer action is None runs no analyzer and records the
            // episode as analyzed, the only trace a run leaves without media to scan.
            await database.SetAnalyzerActionAsync(seasonId, new Dictionary<AnalysisMode, AnalyzerAction> { [AnalysisMode.Introduction] = AnalyzerAction.None });
            var resolver = EntrypointTestHelpers.CreateSeasonResolver(libraryManager);
            var analyzer = new BaseItemAnalyzerTask(
                NullLoggerFactory.Instance,
                resolver,
                new StubFFmpegService { VersionCheck = () => false },
                DatabaseTestHelpers.CreateTempCacheService(),
                cacheDatabase: null!,
                database);

            if (byEpisodeId)
            {
                await analyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None, [episode.Id]);
            }
            else
            {
                await analyzer.AnalyzeSeasonAsync(resolver.ResolveKey(seasonId)!, CancellationToken.None);
            }

            var snapshot = await database.GetSeasonQueueSnapshotAsync(seasonId, [episode.Id]);
            Assert.Contains((episode.Id, AnalysisMode.Introduction), snapshot.AnalysisRecords.Keys);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    /// <summary>
    /// The credits window is set when the season enters the credits pass, for the settled
    /// sibling too since its cached fingerprints are keyed by the same window, and the
    /// audio duration is probed only when the setting is on.
    /// </summary>
    [Theory]
    [InlineData(true, 2, 1300)]
    [InlineData(false, 0, 1320)]
    public async Task CreditsMode_SetsTheCreditsWindowForEverySeasonEpisode(bool probeAudioDuration, int expectedProbes, int expectedEnd)
    {
        var config = new PluginConfiguration { ProbeAudioDuration = probeAudioDuration };
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var (ffmpeg, task) = CreateCreditsRun(config);
        var settled = CreditsEpisode(episodeNumber: 1);
        settled.SetAnalyzed(AnalysisMode.Credits, EpisodeState.NoSegments);
        var pending = CreditsEpisode(episodeNumber: 2);

        await task.AnalyzeItemsAsync([settled, pending], AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(expectedProbes, ffmpeg.ProbeCalls);
        Assert.All(new[] { settled, pending }, episode =>
        {
            Assert.Equal(expectedEnd, episode.CreditsFingerprintEnd);
            Assert.Equal(expectedEnd - config.MaximumCreditsDuration, episode.CreditsFingerprintStart);
        });
    }

    [Fact]
    public async Task CreditsMode_DoesNotProbeASettledSeason()
    {
        var config = new PluginConfiguration { ProbeAudioDuration = true };
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var (ffmpeg, task) = CreateCreditsRun(config);
        var settled = CreditsEpisode(episodeNumber: 1);
        settled.SetAnalyzed(AnalysisMode.Credits, EpisodeState.NoSegments);

        await task.AnalyzeItemsAsync([settled], AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: false, CancellationToken.None);

        Assert.Equal(0, ffmpeg.ProbeCalls);
        Assert.Equal(0, settled.CreditsFingerprintEnd);
    }

    private static readonly Guid CreditsSeasonId = Guid.NewGuid();

    private static QueuedEpisode CreditsEpisode(int episodeNumber) => new()
    {
        EpisodeId = Guid.NewGuid(),
        SeasonId = CreditsSeasonId,
        SeriesId = Guid.NewGuid(),
        SeasonNumber = 1,
        EpisodeNumber = episodeNumber,
        Name = $"Episode {episodeNumber}",
        Path = $"/media/episode-{episodeNumber}.mkv",
        Duration = 1320,
    };

    // A credits run without chromaprint whose scans find nothing, so only the window
    // and the probe are observable.
    private static (StubFFmpegService Ffmpeg, BaseItemAnalyzerTask Task) CreateCreditsRun(PluginConfiguration config)
    {
        var ffmpeg = new StubFFmpegService
        {
            VersionCheck = () => false,
            AudioDuration = _ => 1300,
            CreditsBlackFrames = (_, _) => [],
            KeyframeVisuals = _ => [],
            RangeBlackFrames = (_, _, _, _, _) => [],
            Silence = (_, _, _) => [],
        };
        var task = new BaseItemAnalyzerTask(
            NullLoggerFactory.Instance,
            seasonResolver: null!,
            ffmpeg,
            DatabaseTestHelpers.CreateTempCacheService(),
            cacheDatabase: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());
        return (ffmpeg, task);
    }
}
