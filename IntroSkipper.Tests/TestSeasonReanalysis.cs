// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestSeasonReanalysisPlanner
{
    private const int DefaultDelay = PluginConfiguration.DefaultSettledSeasonDelayHours;
    private const int Minimum = SeasonReanalysisPlanner.MinimumEpisodes;

    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class RecordingProgress : IProgress<double>
    {
        public double? Value { get; private set; }

        public void Report(double value) => Value = value;
    }

    [Fact]
    public async Task AnalyzeItemsAsync_EmptySeasonSet_DoesNotEnumerateLibrary()
    {
        // The test proxy throws on enumeration calls such as GetVirtualFolders.
        var libraryManager = EntrypointTestHelpers.CreateLibraryManager();
        var progress = new RecordingProgress();
        var analyzer = new AnalyzerTaskFactory(
            NullLoggerFactory.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            cacheService: DatabaseTestHelpers.CreateTempCacheService(),
            database: DatabaseTestHelpers.CreateTempSegmentDatabase()).CreateAnalyzerTask();

        await analyzer.AnalyzeItemsAsync(
            progress,
            CancellationToken.None,
            []);

        Assert.Equal(100, progress.Value);
    }

    // Columns: enabled, episode count, hours since the newest episode was added, configured
    // delay, category, season number, AnalyzeSeasonZero, IsExcluded, expected.
    [Theory]
    [InlineData(false, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, true)]
    [InlineData(true, Minimum, 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay - 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, true)]
    [InlineData(true, Minimum, 47, 48, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, 48, 48, QueuedMediaCategory.Episode, 1, false, false, true)]
    [InlineData(true, Minimum, 0, 0, QueuedMediaCategory.Episode, 1, false, false, true)]
    [InlineData(true, Minimum - 1, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Movie, 1, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 1, false, true, true)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 0, false, false, false)]
    [InlineData(true, Minimum, DefaultDelay + 1, DefaultDelay, QueuedMediaCategory.Episode, 0, true, false, true)]
    public void IsSettledForReanalysis(
        bool enabled,
        int episodeCount,
        int hoursSinceNewest,
        int delayHours,
        QueuedMediaCategory category,
        int seasonNumber,
        bool analyzeSeasonZero,
        bool excluded,
        bool expected)
    {
        var config = new PluginConfiguration
        {
            ReanalyzeSettledSeasons = enabled,
            SettledSeasonDelayHours = delayHours,
            AnalyzeSeasonZero = analyzeSeasonZero,
        };
        var season = Season(episodeCount, Now.AddHours(-hoursSinceNewest), category, seasonNumber, excluded);

        Assert.Equal(expected, SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void EpisodeAvailabilityDate_IgnoresMetadataSaveTime()
    {
        var created = Now.AddDays(-30);
        var saved = Now.AddMinutes(-1);
        var episode = new Episode
        {
            DateCreated = created,
            DateLastSaved = saved,
        };

        Assert.Equal(created, QueueManager.EpisodeAvailabilityDate(episode));
    }

    [Fact]
    public void EpisodeAvailabilityDate_FallsBackToMetadataSaveTime_WhenCreatedIsMissing()
    {
        var saved = Now.AddMinutes(-1);
        var episode = new Episode
        {
            DateLastSaved = saved,
        };

        Assert.Equal(saved, QueueManager.EpisodeAvailabilityDate(episode));
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Default, false, false)]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Chromaprint, false, false)]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Chapter, false, true)]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Default, true, true)]
    [InlineData(AnalysisMode.Credits, AnalyzerAction.Default, false, true)]
    public void CanSettleReanalysisRun_SkipsIntroduction_WhenChromaprintUnavailable(
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        bool expected)
    {
        Assert.Equal(expected, BaseItemAnalyzerTask.CanSettleReanalysisRun(mode, action, ffmpegValid));
    }

    [Fact]
    public void ExpandSettledResetModesForDerivedSegments_AddsPreview_WhenCreditsGenerateAnimePreview()
    {
        var resetModes = BaseItemAnalyzerTask.ExpandSettledResetModesForDerivedSegments([AnalysisMode.Credits], true);

        Assert.Equal([AnalysisMode.Credits, AnalysisMode.Preview], resetModes);
    }

    [Fact]
    public void ExpandSettledResetModesForDerivedSegments_DoesNotAddPreview_WhenDisabledOrAlreadyPresent()
    {
        var disabled = BaseItemAnalyzerTask.ExpandSettledResetModesForDerivedSegments([AnalysisMode.Credits], false);
        var alreadyPresent = BaseItemAnalyzerTask.ExpandSettledResetModesForDerivedSegments(
            [AnalysisMode.Credits, AnalysisMode.Preview],
            true);

        Assert.Equal([AnalysisMode.Credits], disabled);
        Assert.Equal([AnalysisMode.Credits, AnalysisMode.Preview], alreadyPresent);
    }

    // NoSegments is a negative-cache result for the current configuration: the season pass
    // is skipped until an episode is reset to NotAnalyzed (new episode, config or
    // Chromaprint-availability change, settled-season reanalysis), and one such episode
    // reopens the whole season, giving the settled NoSegments episodes another chance via
    // NeedsAnalysis().
    [Theory]
    [InlineData(new[] { EpisodeState.NoSegments }, false)]
    [InlineData(new[] { EpisodeState.NoSegments, EpisodeState.NotAnalyzed }, true)]
    [InlineData(new[] { EpisodeState.Analyzed, EpisodeState.UserProvided }, false)]
    public void HasUncachedAnalysisWork(EpisodeState[] states, bool expected)
    {
        var episodes = states.Select(state =>
        {
            var episode = new QueuedEpisode();
            episode.SetAnalyzed(AnalysisMode.Introduction, state);
            return episode;
        }).ToList();

        Assert.Equal(expected, BaseItemAnalyzerTask.HasUncachedAnalysisWork(episodes, AnalysisMode.Introduction));
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    [InlineData(AnalysisMode.Recap)]
    public void AnalysisHash_ChangesWithChromaprintAvailability_ForChromaprintModes(AnalysisMode mode)
    {
        var config = new PluginConfiguration();

        var withChromaprint = ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, ffmpegValid: true);
        var withoutChromaprint = ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, ffmpegValid: false);

        Assert.NotEqual(withChromaprint, withoutChromaprint);
    }

    [Theory]
    [InlineData(AnalysisMode.Preview)]
    [InlineData(AnalysisMode.Commercial)]
    public void AnalysisHash_IgnoresChromaprintAvailability_ForChapterOnlyModes(AnalysisMode mode)
    {
        var config = new PluginConfiguration();

        var withChromaprint = ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, ffmpegValid: true);
        var withoutChromaprint = ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, ffmpegValid: false);

        Assert.Equal(withChromaprint, withoutChromaprint);
    }

    private static List<QueuedEpisode> Season(
        int count,
        DateTime newestAdded,
        QueuedMediaCategory category = QueuedMediaCategory.Episode,
        int seasonNumber = 1,
        bool excluded = false)
    {
        var episodes = new List<QueuedEpisode>(count);
        for (var i = 0; i < count; i++)
        {
            episodes.Add(new QueuedEpisode
            {
                EpisodeId = Guid.NewGuid(),
                SeasonNumber = seasonNumber,
                Category = category,
                IsExcluded = excluded,
                DateAdded = newestAdded.AddHours(-i), // index 0 is the most recent, the Max
            });
        }

        return episodes;
    }
}

public sealed class TestSeasonReanalysisReset
{
    [Fact]
    public async Task SettleReanalysisGuard_PersistsCompletedEpisodeSet()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".db");
        var season = Guid.NewGuid();
        var firstFive = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var firstSix = firstFive.Append(Guid.NewGuid()).ToArray();
        var replacementFive = firstFive.Skip(1).Append(Guid.NewGuid()).ToArray();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.MigrateAsync();
            }

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            // Stays eligible until the work is explicitly recorded, so a failed reset is retried.
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive));

            await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstFive);
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive)); // already done for this set
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, [.. firstFive.Reverse()])); // order-insensitive set match
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSix)); // grew: eligible again
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, replacementFive)); // same count, different membership
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstFive)); // unrecorded mode stays eligible

            await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstSix);
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive)); // shrank: eligible again
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSix));

            // A fresh facade over the same file simulates a plugin restart: the recorded
            // episode sets must be read back from the database.
            var reopenedDatabase = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            Assert.False(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Introduction, firstSix));
            Assert.True(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Introduction, firstFive));
            Assert.True(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Credits, firstFive));
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ResetItemsForReanalysisAsync_DeletesAutomaticSegmentsOfResetModes_PreservesUserRowsAndOtherModes()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var autoEpisode = Guid.NewGuid();
        var userEpisode = Guid.NewGuid();
        var mixedEpisode = Guid.NewGuid();
        var userPreviewEpisode = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.MigrateAsync();

                // Automatic intro: deleted.
                db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.Chapter));

                // An episode holding both an automatic and a user intro is UserProvided for
                // the mode: the analyzers skip it, so the reset must keep its automatic row
                // (nothing would regenerate it).
                db.Segments.Add(new DbSegment(mixedEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(60), SegmentSource.Chromaprint));
                db.Segments.Add(new DbSegment(mixedEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(300), TickConversions.FromSeconds(330), SegmentSource.User));

                // Tombstoned (user-deleted) automatic intro of a reset mode: survives the
                // reset so the deleted range stays gone after re-analysis.
                db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(60), TickConversions.FromSeconds(90), SegmentSource.Chapter)
                {
                    State = SegmentState.Suppressed,
                });

                // User-provided intro: preserved.
                db.Segments.Add(new DbSegment(userEpisode, AnalysisMode.Introduction, TickConversions.FromSeconds(0), TickConversions.FromSeconds(30), SegmentSource.User));

                // Automatic recap on the same episode: a mode outside the reset, preserved.
                db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Recap, TickConversions.FromSeconds(40), TickConversions.FromSeconds(60), SegmentSource.Chapter));

                // Automatic credits and the preview derived from them: both deleted, the
                // preview through the derived-mode expansion. The user preview is preserved.
                db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Credits, TickConversions.FromSeconds(1000), TickConversions.FromSeconds(1100), SegmentSource.Chapter));
                db.Segments.Add(new DbSegment(autoEpisode, AnalysisMode.Preview, TickConversions.FromSeconds(1100), TickConversions.FromSeconds(1320), SegmentSource.CreditsDerived));
                db.Segments.Add(new DbSegment(userPreviewEpisode, AnalysisMode.Preview, TickConversions.FromSeconds(1100), TickConversions.FromSeconds(1320), SegmentSource.User));

                db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Chromaprint));
                db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Recap, AnalyzerAction.Default));
                db.AnalyzedItems.AddRange(
                    new DbAnalyzedItem(autoEpisode, AnalysisMode.Introduction, "hash"),
                    new DbAnalyzedItem(userEpisode, AnalysisMode.Introduction, "hash"),
                    new DbAnalyzedItem(mixedEpisode, AnalysisMode.Introduction, "hash"),
                    new DbAnalyzedItem(autoEpisode, AnalysisMode.Recap, "recap-hash"),
                    new DbAnalyzedItem(autoEpisode, AnalysisMode.Credits, "credits-hash"),
                    new DbAnalyzedItem(userPreviewEpisode, AnalysisMode.Credits, "credits-hash"),
                    new DbAnalyzedItem(autoEpisode, AnalysisMode.Preview, "preview-hash"),
                    new DbAnalyzedItem(userPreviewEpisode, AnalysisMode.Preview, "preview-hash"));

                await db.SaveChangesAsync();
            }

            var cacheDbPath = DatabaseTestHelpers.CreateTempCacheDbPath();
            using (var cacheDb = new DetectionCacheDbContext(cacheDbPath))
            {
                cacheDb.EnsureSchema();
                cacheDb.DetectionCache.Add(new DbDetectionCache(
                    autoEpisode,
                    AnalysisMode.Introduction,
                    CacheEntryType.Chromaprint,
                    EntrypointTestHelpers.EmptyJsonArray));
                await cacheDb.SaveChangesAsync();
            }

            IReadOnlyCollection<AnalysisMode> resetModes = [
                AnalysisMode.Introduction,
                .. BaseItemAnalyzerTask.ExpandSettledResetModesForDerivedSegments([AnalysisMode.Credits], true)
            ];
            await DatabaseTestHelpers.CreateSegmentDatabase(dbPath).ResetItemsForReanalysisAsync(
                [autoEpisode, userEpisode, mixedEpisode, userPreviewEpisode],
                resetModes);

            // The reset reuses the cached fingerprints; only the derived results go.
            using (var cacheDb = new DetectionCacheDbContext(cacheDbPath))
            {
                Assert.True(cacheDb.DetectionCache.Any(e => e.ItemId == autoEpisode && e.Mode == AnalysisMode.Introduction));
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.False(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Introduction && s.State == SegmentState.Active));
                Assert.True(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Introduction && s.State == SegmentState.Suppressed));
                Assert.True(db.Segments.Any(s => s.ItemId == userEpisode && s.Type == AnalysisMode.Introduction && s.Source == SegmentSource.User));
                Assert.True(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Recap));
                Assert.Equal(2, db.Segments.Count(s => s.ItemId == mixedEpisode && s.Type == AnalysisMode.Introduction && s.State == SegmentState.Active));
                Assert.False(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Credits));
                Assert.False(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Preview));
                Assert.True(db.Segments.Any(s => s.ItemId == userPreviewEpisode && s.Type == AnalysisMode.Preview && s.Source == SegmentSource.User));

                // The reset modes' records are gone (every item is NotAnalyzed again); the
                // season's action and the other mode's record survive.
                var recap = Assert.Single(await db.AnalyzedItems.ToListAsync());
                Assert.Equal((autoEpisode, AnalysisMode.Recap, "recap-hash"), (recap.ItemId, recap.Type, recap.ConfigHash));
                var state = await db.SeasonStates.SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
                Assert.Equal(AnalyzerAction.Chromaprint, state.Action);
            }
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task SeasonStateAndAnalysisRecords_UpsertWithoutClobberingEachOther()
    {
        var dbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var episodeA = Guid.NewGuid();
        var episodeB = Guid.NewGuid();
        var completedEpisodeIds = new[] { episodeA, episodeB, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var lowerEpisodeIds = new[] { episodeA, episodeB, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.MigrateAsync();
            }

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [episodeA, episodeB], "hash-a");
            await database.SetAnalyzerActionAsync(
                seasonId,
                new Dictionary<AnalysisMode, AnalyzerAction>
                {
                    [AnalysisMode.Introduction] = AnalyzerAction.Chromaprint,
                });
            await database.RecordSettleReanalysisAsync(seasonId, [AnalysisMode.Introduction], completedEpisodeIds);
            await database.RecordSettleReanalysisAsync(seasonId, [AnalysisMode.Introduction], lowerEpisodeIds);
            await database.ClearItemAnalysisAsync(episodeA, AnalysisMode.Introduction);
            await database.ClearItemAnalysisAsync(Guid.NewGuid(), AnalysisMode.Introduction);

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var state = await db.SeasonStates.SingleAsync();
                Assert.Equal(seasonId, state.SeasonId);
                Assert.Equal(AnalysisMode.Introduction, state.Type);
                Assert.Equal(AnalyzerAction.Chromaprint, state.Action);
                Assert.Equal(lowerEpisodeIds, state.SettledReanalysisEpisodeIds);

                var analyzed = await db.AnalyzedItems.SingleAsync();
                Assert.Equal(episodeB, analyzed.ItemId);
                Assert.Equal("hash-a", analyzed.ConfigHash);
            }
        }
        finally
        {
            DatabaseTestHelpers.DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task VerifyQueueAsync_ReopensNoSegments_WhenChromaprintBecomesAvailable()
    {
        var config = new PluginConfiguration();
        using var fixture = new VerifyQueueFixture(config);
        var withoutChromaprintHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: false);

        using (var db = new IntroSkipperDbContext(fixture.DbPath))
        {
            await db.Database.MigrateAsync();
            db.SeasonStates.Add(new DbSeasonState(fixture.SeasonId, AnalysisMode.Introduction, AnalyzerAction.Default));
            db.AnalyzedItems.Add(new DbAnalyzedItem(fixture.EpisodeId, AnalysisMode.Introduction, withoutChromaprintHash));
            await db.SaveChangesAsync();
        }

        var stillUnavailable = await fixture.VerifyAsync(ffmpegValid: false);
        Assert.Equal(EpisodeState.NoSegments, stillUnavailable.Single().GetAnalyzed(AnalysisMode.Introduction));

        var reopened = await fixture.VerifyAsync(ffmpegValid: true);
        Assert.Equal(EpisodeState.NotAnalyzed, reopened.Single().GetAnalyzed(AnalysisMode.Introduction));
    }

    [Fact]
    public async Task VerifyQueueAsync_StillAnalyzesDisabledItem()
    {
        using var fixture = new VerifyQueueFixture(new PluginConfiguration());

        using (var db = new IntroSkipperDbContext(fixture.DbPath))
        {
            await db.Database.MigrateAsync();
            db.DisabledItems.Add(new DbDisabledItem(fixture.SeasonId, fixture.EpisodeId));
            await db.SaveChangesAsync();
        }

        var verified = await fixture.VerifyAsync(ffmpegValid: false);

        // The disable flag only withholds output from Jellyfin; the analysis
        // pipeline must keep processing the episode.
        Assert.Equal(fixture.EpisodeId, Assert.Single(verified).EpisodeId);
    }

    // Mirrors the production eligibility decision in BaseItemAnalyzerTask.GetSettleReanalysisModesAsync,
    // exercising the same batch read (GetSettleReanalysisStatesAsync) and set comparison the analyzer uses.
    private static async Task<bool> ShouldReanalyzeAsync(
        IntroSkipperDatabase database,
        Guid seasonId,
        AnalysisMode mode,
        IReadOnlyCollection<Guid> episodeIds)
    {
        var states = await database.GetSettleReanalysisStatesAsync(seasonId);
        return !states.TryGetValue(mode, out var state)
            || AnalysisHelpers.ShouldSettleReanalyze(state.SettledReanalysisEpisodeIds, episodeIds);
    }

    /// <summary>
    /// One queued episode whose media file exists on disk, resolvable through the
    /// plugin instance's library manager, over a fresh segment database.
    /// </summary>
    private sealed class VerifyQueueFixture : IDisposable
    {
        private readonly EntrypointTestHelpers.PluginInstanceScope _scope;
        private readonly Episode _episode;
        private readonly string _mediaPath;

        public VerifyQueueFixture(PluginConfiguration config)
        {
            DbPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".db");
            _mediaPath = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".mkv");
            File.WriteAllText(_mediaPath, string.Empty);

            _scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            var plugin = Plugin.Instance!;
            EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", config);

            _episode = new Episode();
            EntrypointTestHelpers.SetPropertyOrField(_episode, "Id", EpisodeId);
            EntrypointTestHelpers.SetPropertyOrField(_episode, "Path", _mediaPath);
            EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager(_episode));
        }

        public string DbPath { get; }

        public Guid SeasonId { get; } = Guid.NewGuid();

        public Guid EpisodeId { get; } = Guid.NewGuid();

        public Task<IReadOnlyList<QueuedEpisode>> VerifyAsync(bool ffmpegValid)
        {
            var queueManager = new QueueManager(
                NullLogger<QueueManager>.Instance,
                EntrypointTestHelpers.CreateLibraryManager(_episode),
                providerManager: null!,
                fileSystem: null!,
                ffmpegService: new FakeFfmpegService(ffmpegValid),
                database: DatabaseTestHelpers.CreateSegmentDatabase(DbPath));
            var queued = new QueuedEpisode
            {
                EpisodeId = EpisodeId,
                SeasonId = SeasonId,
                Name = "S01E01",
                SeriesName = "Rick and Morty",
            };
            return queueManager.VerifyQueueAsync([queued], [AnalysisMode.Introduction]);
        }

        public void Dispose()
        {
            _scope.Dispose();
            File.Delete(_mediaPath);
            DatabaseTestHelpers.DeleteSqliteFiles(DbPath);
        }
    }

    /// <summary>Answers the capability probe; every media operation is unsupported.</summary>
    private sealed class FakeFfmpegService(bool ffmpegValid) : IFFmpegService
    {
        public Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(ffmpegValid);

        public FFmpegCheckResult GetCheckResult() => FFmpegCheckResult.NotRun;

        public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, TimeRange range, int minimum, int threshold, AnalysisMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
