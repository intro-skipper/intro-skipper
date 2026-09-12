// SPDX-FileCopyrightText: 2026 Intro Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Integrations;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestSkipMeIntegration
{
    [Theory]
    [InlineData(AnalysisMode.Introduction, MediaSegmentType.Intro)]
    [InlineData(AnalysisMode.Credits, MediaSegmentType.Outro)]
    [InlineData(AnalysisMode.Recap, MediaSegmentType.Recap)]
    [InlineData(AnalysisMode.Preview, MediaSegmentType.Preview)]
    [InlineData(AnalysisMode.Commercial, MediaSegmentType.Commercial)]
    public async Task AuthoritativeMatches_BypassLocalAnalyzersAndBoundaryAdjustments(AnalysisMode mode, MediaSegmentType type)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            IntroStartOffset = 4,
            IntroEndOffset = -4,
            AdjustIntroBasedOnSilence = true,
            SnapToKeyframe = true,
        });
        using var db = new TempSegmentDb();
        var episode = Episode();
        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [Dto(episode, type, 12.345, 30.678)]);
        var ffmpeg = new StubFFmpegService();
        await db.Database.SetAnalyzerActionAsync(episode.SeasonId, new Dictionary<AnalysisMode, AnalyzerAction> { [mode] = AnalyzerAction.Chromaprint });

        await Task(db.Database, ffmpeg).AnalyzeItemsAsync([episode], mode, AnalyzerAction.Chromaprint, true, CancellationToken.None);

        var row = Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId));
        Assert.Equal(SegmentSource.SkipMe, row.Source);
        Assert.Equal(TimeSpan.FromSeconds(12.345).Ticks, row.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(30.678).Ticks, row.EndTicks);
        Assert.Equal(0, ffmpeg.FingerprintCalls + ffmpeg.CreditsScanCalls + ffmpeg.RangeScanCalls + ffmpeg.VisualScanCalls);
        episode.SetAnalyzed(mode, EpisodeState.NotAnalyzed);
        var snapshot = await db.Database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        new QueueVerifier(Plugin.Instance!.Configuration, [mode], snapshot, true).Classify(episode);
        Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(mode));
    }

    [Fact]
    public async Task MissingMode_FallsBackToChapters_WhileOtherModesStayAuthoritative()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(ChapterConfig(), Chapters());
        using var db = new TempSegmentDb();
        var episode = Episode();
        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [Dto(episode, MediaSegmentType.Outro, 900, 950)]);

        await Task(db.Database).AnalyzeItemsAsync([episode], AnalysisMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);

        var row = Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId));
        Assert.Equal(SegmentSource.Chapter, row.Source);
        Assert.Equal(TimeSpan.FromSeconds(20).Ticks, row.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(80).Ticks, row.EndTicks);
    }

    [Fact]
    public async Task ManualSegments_AreNotReplacedBySkipMe()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        using var db = new TempSegmentDb();
        var manual = Episode();
        var pending = Episode();
        foreach (var episode in new[] { manual, pending })
        {
            episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [Dto(episode, MediaSegmentType.Intro, 20, 80)]);
        }

        await db.Database.SeedUserSegmentAsync(manual.EpisodeId, AnalysisMode.Introduction, TimeSpan.FromSeconds(5).Ticks, TimeSpan.FromSeconds(15).Ticks);
        manual.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.UserProvided);

        await Task(db.Database).AnalyzeItemsAsync([manual, pending], AnalysisMode.Introduction, AnalyzerAction.Default, true, CancellationToken.None);

        var row = Assert.Single(await db.Database.GetSegmentsAsync(manual.EpisodeId));
        Assert.Equal(SegmentSource.User, row.Source);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, row.StartTicks);
        Assert.Equal(SegmentSource.SkipMe, Assert.Single(await db.Database.GetSegmentsAsync(pending.EpisodeId)).Source);
    }

    [Fact]
    public async Task DisabledMode_DoesNotImportSkipMe()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        using var db = new TempSegmentDb();
        var episode = Episode();
        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [Dto(episode, MediaSegmentType.Intro, 20, 80)]);

        await Task(db.Database).AnalyzeItemsAsync([episode], AnalysisMode.Introduction, AnalyzerAction.None, true, CancellationToken.None);

        Assert.Empty(await db.Database.GetSegmentsAsync(episode.EpisodeId));
        var snapshot = await db.Database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        Assert.Equal(ConfigHasher.Analysis(Plugin.Instance!.Configuration, AnalysisMode.Introduction, AnalyzerAction.None, true), snapshot.AnalyzedConfigHashes[(episode.EpisodeId, AnalysisMode.Introduction)]);
    }

    [Fact]
    public async Task ChangedOrRemovedMatches_ReopenOnlyAffectedModes_AndReplaceStaleResults()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(ChapterConfig(), Chapters());
        using var db = new TempSegmentDb();
        var episode = Episode();
        var config = Plugin.Instance!.Configuration;
        var task = Task(db.Database);
        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [Dto(episode, MediaSegmentType.Intro, 10, 60)]);
        await task.AnalyzeItemsAsync([episode], AnalysisMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);
        await db.Database.MarkItemsAnalyzedAsync(AnalysisMode.Recap, [episode.EpisodeId], ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.Default, false));

        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [Dto(episode, MediaSegmentType.Intro, 15, 65)]);
        episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NotAnalyzed);
        var snapshot = await db.Database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        new QueueVerifier(config, [AnalysisMode.Introduction, AnalysisMode.Recap], snapshot, false).Classify(episode);
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Recap));

        await task.AnalyzeItemsAsync([episode], AnalysisMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(15).Ticks, Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId)).StartTicks);

        episode.SkipMe = null;
        episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NotAnalyzed);
        snapshot = await db.Database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        new QueueVerifier(config, [AnalysisMode.Introduction], snapshot, false).Classify(episode);
        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
        await task.AnalyzeItemsAsync([episode], AnalysisMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);
        Assert.Equal(SegmentSource.Chapter, Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId)).Source);
    }

    [Fact]
    public async Task NewMatch_ReopensNegativeCache()
    {
        using var db = new TempSegmentDb();
        var config = new PluginConfiguration();
        var episode = Episode();
        await db.Database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [episode.EpisodeId], ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, false));
        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [Dto(episode, MediaSegmentType.Intro, 20, 80)]);

        var snapshot = await db.Database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        new QueueVerifier(config, [AnalysisMode.Introduction], snapshot, false).Classify(episode);

        Assert.Equal(EpisodeState.NotAnalyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Fact]
    public async Task SuppressedMatch_DoesNotFallBackToLocalDetection()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(ChapterConfig(), Chapters());
        using var db = new TempSegmentDb();
        var episode = Episode();
        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [Dto(episode, MediaSegmentType.Intro, 10, 15)]);
        await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Introduction, episode.SkipMe.GetSegments(AnalysisMode.Introduction), SegmentSource.SkipMe);
        var row = Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId));
        await db.Database.DeleteSegmentAsync(episode.EpisodeId, row.Id);

        await Task(db.Database).AnalyzeItemsAsync([episode], AnalysisMode.Introduction, AnalyzerAction.Default, true, CancellationToken.None);

        Assert.Empty(await db.Database.GetSegmentsAsync(episode.EpisodeId));
        Assert.Equal(SegmentState.Suppressed, Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId, includeSuppressed: true)).State);
        Assert.Equal(EpisodeState.NoSegments, episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Fact]
    public async Task MixedSeason_DoesNotFingerprintAuthoritativeNeighbors()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(ChapterConfig(), Chapters());
        using var db = new TempSegmentDb();
        var authoritative = Episode();
        var local = Episode();
        local.SeasonId = authoritative.SeasonId;
        authoritative.SkipMe = new SkipMeSnapshot(authoritative.EpisodeId, authoritative.Duration, [Dto(authoritative, MediaSegmentType.Intro, 10, 60)]);
        var ffmpeg = new StubFFmpegService();

        await Task(db.Database, ffmpeg).AnalyzeItemsAsync([authoritative, local], AnalysisMode.Introduction, AnalyzerAction.Chromaprint, true, CancellationToken.None);

        Assert.Equal(0, ffmpeg.FingerprintCalls);
        Assert.Equal(SegmentSource.SkipMe, Assert.Single(await db.Database.GetSegmentsAsync(authoritative.EpisodeId)).Source);
        Assert.Equal(SegmentSource.Chapter, Assert.Single(await db.Database.GetSegmentsAsync(local.EpisodeId)).Source);
    }

    [Fact]
    public async Task QueueVerification_SnapshotsSource_AndDoesNotTurnLookupFailuresIntoNoMatches()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        using var db = new TempSegmentDb();
        var path = Path.GetTempFileName();
        try
        {
            var queued = Episode();
            var item = JellyfinItems.Episode(queued.EpisodeId, Guid.NewGuid(), queued.SeasonId, path: path);
            var library = EntrypointTestHelpers.CreateLibraryManager(item);
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", library);
            var source = new Source { Segments = [Dto(queued, MediaSegmentType.Intro, 10, 60)] };
            var services = new ServiceCollection();
            SkipMeIntegration.RegisterV1(services, _ => source);
            using var provider = services.BuildServiceProvider();
            var queueManager = new QueueManager(NullLogger<QueueManager>.Instance, library, null!, null!, new StubFFmpegService { VersionCheck = () => false }, db.Database, provider.GetRequiredService<SkipMeIntegration>());

            Assert.Single(await queueManager.VerifyQueueAsync([queued], [AnalysisMode.Introduction]));
            source.Segments = [];
            await Task(db.Database).AnalyzeItemsAsync([queued], AnalysisMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None);
            var row = Assert.Single(await db.Database.GetSegmentsAsync(queued.EpisodeId));
            Assert.Equal(SegmentSource.SkipMe, row.Source);

            source.Failure = new IOException("Source temporarily unavailable");
            Assert.Empty(await queueManager.VerifyQueueAsync([queued], [AnalysisMode.Introduction]));
            Assert.Equal(row.Id, Assert.Single(await db.Database.GetSegmentsAsync(queued.EpisodeId)).Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AuthoritativePreview_ReplacesDerivedPreview_AndIsNotExpandedByCredits()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AnimePreviewFromCreditsEnd = true });
        using var db = new TempSegmentDb();
        var episode = Episode();
        episode.Category = QueuedMediaCategory.AnimeEpisode;
        episode.SkipMe = new SkipMeSnapshot(episode.EpisodeId, episode.Duration,
            [Dto(episode, MediaSegmentType.Outro, 850, 900), Dto(episode, MediaSegmentType.Preview, 920, 960)]);
        await db.Database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Preview, [new Segment(episode.EpisodeId, new TimeRange(900, 1000))], SegmentSource.CreditsDerived);
        var task = Task(db.Database);

        await task.AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, true, CancellationToken.None);
        await task.AnalyzeItemsAsync([episode], AnalysisMode.Credits, AnalyzerAction.Default, true, CancellationToken.None);

        var preview = Assert.Single(await db.Database.GetSegmentsAsync(episode.EpisodeId), row => row.Type == AnalysisMode.Preview);
        Assert.Equal(SegmentSource.SkipMe, preview.Source);
        Assert.Equal(TimeSpan.FromSeconds(920).Ticks, preview.StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(960).Ticks, preview.EndTicks);
    }

    [Fact]
    public void Snapshot_RejectsInvalidRanges_AndHashesCanonicalTicks()
    {
        var episode = Episode();
        var first = Dto(episode, MediaSegmentType.Commercial, 10, 20);
        var second = Dto(episode, MediaSegmentType.Commercial, 30, 40);
        var snapshot = new SkipMeSnapshot(episode.EpisodeId, episode.Duration,
        [
            second, first, first,
            Dto(episode, MediaSegmentType.Intro, -1, 20),
            Dto(episode, MediaSegmentType.Outro, 20, 20),
            Dto(episode, MediaSegmentType.Preview, 30, 20),
            Dto(episode, MediaSegmentType.Recap, 10, 1001),
            Dto(Episode(), MediaSegmentType.Intro, 10, 20),
            Dto(episode, (MediaSegmentType)999, 10, 20),
        ]);
        var reordered = new SkipMeSnapshot(episode.EpisodeId, episode.Duration, [first, second]);

        Assert.Equal(2, snapshot.GetSegments(AnalysisMode.Commercial).Count);
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            Assert.Equal(mode == AnalysisMode.Commercial, snapshot.HasSegments(mode));
            Assert.Equal(ConfigHasher.WithSkipMe("base", mode, AnalyzerAction.Default, reordered), ConfigHasher.WithSkipMe("base", mode, AnalyzerAction.Default, snapshot));
        }
    }

    [Fact]
    public async Task Registration_ResolvesSourceLazily_WithoutRegisteringAJellyfinProvider()
    {
        var services = new ServiceCollection();
        var calls = 0;
        var source = new Source();
        SkipMeIntegration.RegisterV1(services, _ => { calls++; return source; });
        Assert.Equal(0, calls);
        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IMediaSegmentProvider>());
        var integration = provider.GetRequiredService<SkipMeIntegration>();
        Assert.Equal(1, calls);
        var item = JellyfinItems.Episode(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        source.Segments = [new MediaSegmentDto { ItemId = item.Id, Type = MediaSegmentType.Intro, StartTicks = 10, EndTicks = 20 }];

        var snapshot = await integration.ReadAsync(item, 1000, CancellationToken.None);
        Assert.True(snapshot.HasSegments(AnalysisMode.Introduction));
        source.Supported = false;
        Assert.False((await integration.ReadAsync(item, 1000, CancellationToken.None)).HasSegments(AnalysisMode.Introduction));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => integration.ReadAsync(item, 1000, cancellation.Token));
    }

    private static QueuedEpisode Episode() => new()
    {
        EpisodeId = Guid.NewGuid(),
        SeasonId = Guid.NewGuid(),
        Duration = 1000,
        SeasonNumber = 1,
        Category = QueuedMediaCategory.Episode,
        CreditsFingerprintStart = 550,
        CreditsFingerprintEnd = 1000,
    };

    private static MediaSegmentDto Dto(QueuedEpisode episode, MediaSegmentType type, double start, double end)
        => new() { ItemId = episode.EpisodeId, Type = type, StartTicks = TimeSpan.FromSeconds(start).Ticks, EndTicks = TimeSpan.FromSeconds(end).Ticks };

    private static BaseItemAnalyzerTask Task(IIntroSkipperDatabase database, StubFFmpegService? ffmpeg = null)
        => new(NullLogger.Instance, NullLoggerFactory.Instance, null!, ffmpeg ?? new StubFFmpegService(), DatabaseTestHelpers.CreateTempCacheService(), database);

    private static PluginConfiguration ChapterConfig() => new()
    {
        ChapterAnalyzerIntroductionPattern = "Intro",
        AdjustIntroBasedOnChapters = false,
        AdjustIntroBasedOnSilence = false,
        SnapToKeyframe = false,
        IntroStartOffset = 0,
        IntroEndOffset = 0,
    };

    private static ChapterInfo[] Chapters() =>
    [
        new() { Name = "Opening", StartPositionTicks = 0 },
        new() { Name = "Intro", StartPositionTicks = TimeSpan.FromSeconds(20).Ticks },
        new() { Name = "Main", StartPositionTicks = TimeSpan.FromSeconds(80).Ticks },
    ];

    private sealed class Source : IMediaSegmentProvider
    {
        public string Name => "SkipMe.db";

        public bool Supported { get; set; } = true;

        public IReadOnlyList<MediaSegmentDto> Segments { get; set; } = [];

        public Exception? Failure { get; set; }

        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(Supported);

        public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
            => Failure is null ? System.Threading.Tasks.Task.FromResult(Segments) : System.Threading.Tasks.Task.FromException<IReadOnlyList<MediaSegmentDto>>(Failure);

        public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
    }
}
