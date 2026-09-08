namespace IntroSkipper.Tests;

using System;
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

        var analyzer = new AnalyzerTaskFactory(
            NullLoggerFactory.Instance,
            EntrypointTestHelpers.CreateLibraryManager(),
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: FfmpegTestHelpers.CreateFFmpegService(),
            cacheService: null!,
            database: null!).CreateAnalyzerTask();

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

        var task = new BaseItemAnalyzerTask(NullLogger.Instance, NullLoggerFactory.Instance, null!, new StubFFmpegService(), DatabaseTestHelpers.CreateTempCacheService(), database);
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
        var libraryManager = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], [episode]);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", libraryManager);
        var ffmpegService = new StubFFmpegService { VersionCheck = () => false };
        var analyzer = new AnalyzerTaskFactory(
            NullLoggerFactory.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            ffmpegService,
            cacheService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase()).CreateAnalyzerTask();

        await analyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(1, ffmpegService.VersionCheckCalls);
    }
}
