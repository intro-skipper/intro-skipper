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
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestBaseItemAnalyzerTaskOrchestration
{
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

        var task = CreateTask(new StubFFmpegService(), database);
        await task.AnalyzeItemsAsync([episode], AnalysisMode.Credits, AnalyzerAction.Default, false, CancellationToken.None);
        await task.AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        var rows = await database.GetSegmentsAsync(episode.EpisodeId);
        Assert.Equal(SegmentSource.User, Assert.Single(rows, s => s.Type == AnalysisMode.Credits).Source);
        Assert.Equal(expectPreview, rows.Any(s => s.Type == AnalysisMode.Preview));
    }

    /// <summary>
    /// The derive after the credits pass leaves the Preview state open while subtitle
    /// detection in the Preview mode follows in the same pass, so the Preview mode still reads
    /// the subtitles and its match replaces the derived preview.
    /// </summary>
    [Fact]
    public async Task PreviewMode_SubtitlePreviewSupersedesCreditsDerivedPreview()
    {
        var config = SubtitleConfig(derivePreviews: true);
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var episode = SubtitleEpisode();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.SeedUserSegmentAsync(episode.EpisodeId, AnalysisMode.Credits, DatabaseTestHelpers.Ticks(140), DatabaseTestHelpers.Ticks(160));
        var task = CreateTask(CreditsFfmpeg(_ => new([new SubtitleCue(100, 103, "Here's the preview")], Complete: true)), database);

        await task.AnalyzeItemsAsync([episode], AnalysisMode.Credits, AnalyzerAction.Default, false, CancellationToken.None);
        Assert.Equal(SegmentSource.CreditsDerived, Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId), segment => segment.Type == AnalysisMode.Preview).Source);

        await task.AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId), segment => segment.Type == AnalysisMode.Preview);
        Assert.Equal(SegmentSource.Subtitle, preview.Source);
        Assert.Equal(100, TickConversions.ToSeconds(preview.StartTicks));
        Assert.Equal(180, TickConversions.ToSeconds(preview.EndTicks));
        Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public async Task PreviewMode_SubtitleExtractionFailurePreservesStaleFallback()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(SubtitleConfig(), []);
        var episode = SubtitleEpisode();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Preview,
            [new Segment(episode.EpisodeId, new TimeRange(100, 180))],
            SegmentSource.Chapter,
            configHash: "old-config");
        var ffmpeg = new StubFFmpegService { Subtitles = _ => new([], Complete: false) };

        await CreateTask(ffmpeg, database).AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
        Assert.Equal(SegmentSource.Chapter, preview.Source);
        Assert.Equal("old-config", preview.ConfigHash);
        Assert.Equal(EpisodeState.AnalysisFailed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public async Task PreviewMode_SubtitleFailureStillDerivesCreditsFallbackAndRemainsRetryable()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(SubtitleConfig(derivePreviews: true), []);
        var episode = SubtitleEpisode();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Credits,
            [new Segment(episode.EpisodeId, new TimeRange(80, 100))],
            SegmentSource.BlackFrame);
        var ffmpeg = new StubFFmpegService { Subtitles = _ => new([], Complete: false) };

        await CreateTask(ffmpeg, database).AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId), segment => segment.Type == AnalysisMode.Preview);
        Assert.Equal(SegmentSource.CreditsDerived, preview.Source);
        Assert.Equal(100, TickConversions.ToSeconds(preview.StartTicks));
        Assert.Equal(180, TickConversions.ToSeconds(preview.EndTicks));
        Assert.Equal(EpisodeState.AnalysisFailed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    [Fact]
    public async Task PreviewMode_PartialSubtitleMatchRemainsTheOnlyPreviewAndRetryable()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(SubtitleConfig(derivePreviews: true), []);
        var episode = SubtitleEpisode();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Credits,
            [new Segment(episode.EpisodeId, new TimeRange(80, 100))],
            SegmentSource.BlackFrame);
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ => new([new SubtitleCue(110, 113, "Here's the preview")], Complete: false),
        };

        await CreateTask(ffmpeg, database).AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId), segment => segment.Type == AnalysisMode.Preview);
        Assert.Equal(SegmentSource.Subtitle, preview.Source);
        Assert.Equal(110, TickConversions.ToSeconds(preview.StartTicks));
        Assert.Equal(180, TickConversions.ToSeconds(preview.EndTicks));
        Assert.Equal(EpisodeState.AnalysisFailed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    /// <summary>
    /// A rewritten sidecar reopens Preview under an unchanged hash, so the stale cleanup keeps
    /// the subtitle preview. Only the subtitle pass retires it, when a complete scan no longer
    /// matches, and the credits-derived preview takes its place.
    /// </summary>
    [Fact]
    public async Task PreviewMode_CompleteScanWithoutMatch_RetiresTheSubtitlePreviewForTheDerivedOne()
    {
        var config = SubtitleConfig(derivePreviews: true);
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var episode = SubtitleEpisode();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var currentHash = ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, ffmpegValid: false, previewFromCreditsEndOverride: true);
        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Credits,
            [new Segment(episode.EpisodeId, new TimeRange(80, 100))],
            SegmentSource.BlackFrame);
        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Preview,
            [new Segment(episode.EpisodeId, new TimeRange(120, 180))],
            SegmentSource.Subtitle,
            currentHash);
        var ffmpeg = new StubFFmpegService
        {
            Subtitles = _ => new([new SubtitleCue(120, 123, "An ordinary line")], Complete: true),
        };

        await CreateTask(ffmpeg, database).AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        Assert.Equal(currentHash, episode.AnalysisConfigHash);
        var preview = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId), segment => segment.Type == AnalysisMode.Preview);
        Assert.Equal(SegmentSource.CreditsDerived, preview.Source);
        Assert.Equal(100, TickConversions.ToSeconds(preview.StartTicks));
        Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    /// <summary>
    /// With no intro after the cue, whether the intro came first or the episode has none, and
    /// no chapter to end it, the match has no end. Every retry would find the same, so the
    /// prior subtitle recap stays as the fallback and the mode settles instead of reopening
    /// on every pass.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecapMode_SubtitleRecapWithoutAnEnd_KeepsTheStaleRecapAndSettles(bool introFirst)
    {
        var config = SubtitleConfig();
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, Array.Empty<ChapterInfo>());
        var episode = SubtitleEpisode();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        if (introFirst)
        {
            await database.SeedUserSegmentAsync(episode.EpisodeId, AnalysisMode.Introduction, DatabaseTestHelpers.Ticks(20), DatabaseTestHelpers.Ticks(30));
        }

        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Recap,
            [new Segment(episode.EpisodeId, new TimeRange(5, 15))],
            SegmentSource.Subtitle,
            configHash: "old-config");
        var ffmpeg = new StubFFmpegService { Subtitles = _ => new([new SubtitleCue(30, 33, "Previously on the story")], Complete: true) };

        await CreateTask(ffmpeg, database).AnalyzeItemsAsync([episode], AnalysisMode.Recap, AnalyzerAction.Default, false, CancellationToken.None);

        var recap = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId), segment => segment.Type == AnalysisMode.Recap);
        Assert.Equal(5, TickConversions.ToSeconds(recap.StartTicks));
        Assert.Equal(15, TickConversions.ToSeconds(recap.EndTicks));
        Assert.Equal("old-config", recap.ConfigHash);
        var snapshot = await database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        Assert.Equal(episode.AnalysisConfigHash, snapshot.AnalysisRecords[(episode.EpisodeId, AnalysisMode.Recap)].ConfigHash);
    }

    /// <summary>
    /// A pattern .NET rejects matches nothing. The subtitle row it would have replaced stays,
    /// and the mode is retried until the pattern is fixed rather than settled with no segment.
    /// </summary>
    [Fact]
    public async Task InvalidSubtitlePattern_KeepsTheSubtitleRowAndLeavesTheModeOpen()
    {
        var config = SubtitleConfig();
        config.SubtitlePreviewPattern = "[^]";
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var episode = SubtitleEpisode();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Preview,
            [new Segment(episode.EpisodeId, new TimeRange(100, 180))],
            SegmentSource.Subtitle,
            configHash: "old-config");

        await CreateTask(new StubFFmpegService(), database).AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId));
        Assert.Equal(SegmentSource.Subtitle, preview.Source);
        Assert.Equal(EpisodeState.AnalysisFailed, episode.GetAnalyzed(AnalysisMode.Preview));
        var snapshot = await database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        Assert.DoesNotContain((episode.EpisodeId, AnalysisMode.Preview), snapshot.AnalysisRecords.Keys);
    }

    /// <summary>
    /// With subtitle detection active, the stale-row cleanup runs only after the chain, so it
    /// is what removes another pass's row left from an older configuration.
    /// </summary>
    [Fact]
    public async Task CompleteScanWithoutMatch_RemovesTheStaleRowsOfTheMode()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(SubtitleConfig(), []);
        var episode = SubtitleEpisode();
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        await database.ReplaceAutoSegmentsAsync(
            episode.EpisodeId,
            AnalysisMode.Preview,
            [new Segment(episode.EpisodeId, new TimeRange(100, 180))],
            SegmentSource.Chapter,
            configHash: "old-config");
        var ffmpeg = new StubFFmpegService { Subtitles = _ => new([new SubtitleCue(120, 123, "An ordinary line")], Complete: true) };

        await CreateTask(ffmpeg, database).AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        Assert.Empty(await database.GetSegmentsAsync(episode.EpisodeId));
    }

    /// <summary>
    /// A tombstone rejects the subtitle candidate. The old-hash chapter preview stays, which
    /// the cleanup after the chain would otherwise delete, and the mode settles. A later pass
    /// that reopens the mode for a new sibling leaves the settled episode's row alone.
    /// </summary>
    [Fact]
    public async Task RejectedSubtitleMatch_PreservesRowsAndSettlesTheMode()
    {
        var config = SubtitleConfig();
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, Array.Empty<ChapterInfo>());
        var episode = SubtitleEpisode();
        var episodeId = episode.EpisodeId;
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
            [new Segment(episodeId, new TimeRange(120, 130))],
            SegmentSource.Chapter,
            configHash: "old-config");
        var ffmpeg = new StubFFmpegService { Subtitles = _ => new([new SubtitleCue(100, 103, "Here's the preview")], Complete: true) };

        await CreateTask(ffmpeg, database).AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        (SegmentSource, string)[] standing = [(SegmentSource.Chapter, "old-config")];
        Assert.Equal(standing, await RowsAsync());
        Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
        var snapshot = await database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episodeId]);
        Assert.Equal(episode.AnalysisConfigHash, snapshot.AnalysisRecords[(episodeId, AnalysisMode.Preview)].ConfigHash);
        var nextPass = SubtitleEpisode(episodeId);
        new QueueVerifier(config, [AnalysisMode.Preview], snapshot, false, previewFromCreditsEndOverride: false).Classify(nextPass);
        Assert.Equal(EpisodeState.Analyzed, nextPass.GetAnalyzed(AnalysisMode.Preview));

        await CreateTask(ffmpeg, database).AnalyzeItemsAsync([nextPass, SubtitleEpisode()], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        Assert.Equal(standing, await RowsAsync());

        async Task<(SegmentSource, string)[]> RowsAsync()
            => [.. (await database.GetSegmentsAsync(episodeId)).OrderBy(s => s.StartTicks).Select(s => (s.Source, s.ConfigHash))];
    }

    /// <summary>
    /// 12.0.5.0 could store a chapter preview and a credits-derived one for the same episode.
    /// A reopened Preview mode writes the chapter match again, which retires the derived row,
    /// so the episode serves one preview.
    /// </summary>
    [Fact]
    public async Task PreviewMode_ChapterMatchRetiresTheDerivedPreviewBesideIt()
    {
        var config = new PluginConfiguration { AnimePreviewFromCreditsEnd = true, EnableSponsorBlockChapterDetection = false };
        ChapterInfo[] chapters =
        [
            new() { Name = "Episode", StartPositionTicks = 0 },
            new() { Name = "Ending", StartPositionTicks = DatabaseTestHelpers.Ticks(100) },
            new() { Name = "Preview", StartPositionTicks = DatabaseTestHelpers.Ticks(150) },
        ];
        using var scope = EntrypointTestHelpers.CreatePluginScope(config, chapters);
        var episode = SubtitleEpisode();
        using var segmentDb = new TempSegmentDb();
        var database = segmentDb.Database;
        await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Credits, [new Segment(episode.EpisodeId, new TimeRange(100, 140))], SegmentSource.Chapter, "credits");
        await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Preview, [new Segment(episode.EpisodeId, new TimeRange(150, 180))], SegmentSource.Chapter, "old-config");

        // The facade refuses to store both, so the derived row goes in directly.
        await using (var db = segmentDb.Context())
        {
            db.Segments.Add(new DbSegment(episode.EpisodeId, AnalysisMode.Preview, DatabaseTestHelpers.Ticks(140), DatabaseTestHelpers.Ticks(180), SegmentSource.CreditsDerived, "credits"));
            await db.SaveChangesAsync();
        }

        await CreateTask(new StubFFmpegService(), database).AnalyzeItemsAsync([episode], AnalysisMode.Preview, AnalyzerAction.Default, false, CancellationToken.None);

        var preview = Assert.Single(await database.GetSegmentsAsync(episode.EpisodeId), s => s.Type == AnalysisMode.Preview);
        Assert.Equal((SegmentSource.Chapter, 150d), (preview.Source, TickConversions.ToSeconds(preview.StartTicks)));
        Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Preview));
    }

    /// <summary>
    /// The watcher queues changed items by their own id and the run analyzes the season
    /// holding each one. A scan hands over the ids of the episodes it erased.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzeItemsAsync_ScopedToAnEpisodeIdOrASeasonId_AnalyzesThatSeason(bool byEpisodeId)
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

            await analyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None, [byEpisodeId ? episode.Id : seasonId]);

            var snapshot = await database.GetSeasonQueueSnapshotAsync(seasonId, [episode.Id]);
            Assert.Contains((episode.Id, AnalysisMode.Introduction), snapshot.AnalysisRecords.Keys);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    /// <summary>
    /// A season that throws is skipped, not the pass: the other seasons in scope are still
    /// analyzed and recorded, and the failed one is left unrecorded for the next pass.
    /// </summary>
    [Fact]
    public async Task AnalyzeItemsAsync_SkipsASeasonThatThrows_AndAnalyzesTheRest()
    {
        var config = new PluginConfiguration
        {
            ScanIntroduction = false,
            ScanCredits = true,
            ScanRecap = false,
            ScanPreview = false,
            ScanCommercial = false,
            ProbeAudioDuration = true,
        };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(config, []);
        var brokenPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".mkv");
        var soundPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".mkv");
        await File.WriteAllTextAsync(brokenPath, string.Empty);
        await File.WriteAllTextAsync(soundPath, string.Empty);
        try
        {
            var broken = JellyfinItems.Movie(Guid.NewGuid(), name: "Broken", path: brokenPath);
            var sound = JellyfinItems.Movie(Guid.NewGuid(), name: "Sound", path: soundPath);
            var libraryManager = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], [broken, sound]);
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", libraryManager);
            var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
            var ffmpeg = new StubFFmpegService
            {
                VersionCheck = () => false,
                AudioDuration = path => path == brokenPath ? throw new IOException("probe failed") : 1300,
                KeyframeScan = (_, _) => [],
                RangeBlackFrames = (_, _, _, _, _) => [],
                Silence = (_, _, _) => [],
            };
            var analyzer = new BaseItemAnalyzerTask(
                NullLoggerFactory.Instance,
                EntrypointTestHelpers.CreateSeasonResolver(libraryManager),
                ffmpeg,
                DatabaseTestHelpers.CreateTempCacheService(),
                cacheDatabase: null!,
                database);

            await analyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None);

            var brokenSnapshot = await database.GetSeasonQueueSnapshotAsync(broken.Id, [broken.Id]);
            Assert.Empty(brokenSnapshot.AnalysisRecords);
            var soundSnapshot = await database.GetSeasonQueueSnapshotAsync(sound.Id, [sound.Id]);
            Assert.Contains((sound.Id, AnalysisMode.Credits), soundSnapshot.AnalysisRecords.Keys);
        }
        finally
        {
            File.Delete(brokenPath);
            File.Delete(soundPath);
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
        var ffmpeg = CreditsFfmpeg();
        return (ffmpeg, CreateTask(ffmpeg, DatabaseTestHelpers.CreateTempSegmentDatabase()));
    }

    // ffmpeg for a credits pass without chromaprint whose scans find nothing, optionally
    // with subtitle cues for a later Recap or Preview pass.
    private static StubFFmpegService CreditsFfmpeg(Func<QueuedEpisode, SubtitleScan>? subtitles = null) => new()
    {
        VersionCheck = () => false,
        AudioDuration = _ => 1300,
        KeyframeScan = (_, _) => [],
        RangeBlackFrames = (_, _, _, _, _) => [],
        Silence = (_, _, _) => [],
        Subtitles = subtitles,
    };

    // The task a pass builds, for per-mode analysis of verified episodes, which never
    // reaches the season resolver or the detection-cache database.
    private static BaseItemAnalyzerTask CreateTask(StubFFmpegService ffmpeg, IntroSkipperDatabase database) => new(
        NullLoggerFactory.Instance,
        seasonResolver: null!,
        ffmpeg,
        DatabaseTestHelpers.CreateTempCacheService(),
        cacheDatabase: null!,
        database);

    // Subtitle detection on for Recap and Preview with the chapter patterns blank, so the
    // subtitle analyzer and the credits-derived preview are the only producers.
    private static PluginConfiguration SubtitleConfig(bool derivePreviews = false) => new()
    {
        AnimePreviewFromCreditsEnd = derivePreviews,
        EnableSubtitleRecapDetection = true,
        EnableSubtitlePreviewDetection = true,
        MinimumRecapDuration = 5,
        MaximumRecapDuration = 120,
        MinimumPreviewDuration = 5,
        ChapterAnalyzerRecapPattern = string.Empty,
        ChapterAnalyzerPreviewPattern = string.Empty,
        EnableSponsorBlockChapterDetection = false,
    };

    private static readonly Guid SubtitleSeasonId = Guid.NewGuid();

    // An anime episode, so it gets a credits-derived preview when the configuration
    // derives previews. Its recap window spans the whole episode, as a season pass sets it
    // for an episode shorter than five minutes. Every one belongs to the same season.
    private static QueuedEpisode SubtitleEpisode(Guid? episodeId = null) => new()
    {
        EpisodeId = episodeId ?? Guid.NewGuid(),
        SeasonId = SubtitleSeasonId,
        SeasonNumber = 1,
        Category = QueuedMediaCategory.AnimeEpisode,
        Name = "Episode 1",
        Path = "/media/episode-1.mkv",
        Duration = 180,
        IntroFingerprintEnd = 180,
    };
}
