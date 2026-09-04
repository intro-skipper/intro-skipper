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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestSeasonReanalysisPlanner
{
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

    [Fact]
    public void IsSettledForReanalysis_ReturnsFalse_WhenDisabled()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = false };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, SettledTime());

        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_ReturnsTrue_WhenSettledMultiEpisodeSeason()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, SettledTime());

        Assert.True(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_ReturnsFalse_WhenStillReceivingEpisodes()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, Now.AddHours(-1));

        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_UsesDefaultSettledSeasonDelayHours()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };

        var notSettled = Season(
            SeasonReanalysisPlanner.MinimumEpisodes,
            Now.AddHours(-(PluginConfiguration.DefaultSettledSeasonDelayHours - 1)));
        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(notSettled, config, Now));

        var settled = Season(
            SeasonReanalysisPlanner.MinimumEpisodes,
            Now.AddHours(-PluginConfiguration.DefaultSettledSeasonDelayHours));
        Assert.True(SeasonReanalysisPlanner.IsSettledForReanalysis(settled, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_UsesConfiguredSettledSeasonDelayHours()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true, SettledSeasonDelayHours = 48 };

        var notSettled = Season(SeasonReanalysisPlanner.MinimumEpisodes, Now.AddHours(-47));
        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(notSettled, config, Now));

        var settled = Season(SeasonReanalysisPlanner.MinimumEpisodes, Now.AddHours(-48));
        Assert.True(SeasonReanalysisPlanner.IsSettledForReanalysis(settled, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_AllowsImmediateQuietTimeWhenDelayIsZero()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true, SettledSeasonDelayHours = 0 };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, Now);

        Assert.True(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_ReturnsFalse_WhenBelowMinimumEpisodes()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes - 1, SettledTime());

        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_ReturnsFalse_ForMovies()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, SettledTime(), category: QueuedMediaCategory.Movie);

        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_IgnoresExcludedFlag_WhenQueueAlreadyFiltered()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, SettledTime(), excluded: true);

        Assert.True(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_SeasonZero_RespectsAnalyzeSeasonZeroToggle()
    {
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, SettledTime(), seasonNumber: 0);

        var disabled = new PluginConfiguration { ReanalyzeSettledSeasons = true, AnalyzeSeasonZero = false };
        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(season, disabled, Now));

        var enabled = new PluginConfiguration { ReanalyzeSettledSeasons = true, AnalyzeSeasonZero = true };
        Assert.True(SeasonReanalysisPlanner.IsSettledForReanalysis(season, enabled, Now));
    }

    [Fact]
    public void IsSettledForReanalysis_ReturnsFalse_WhenDateAddedIsRecent()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, Now.AddHours(-1));

        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
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

    [Fact]
    public void HasUncachedAnalysisWork_ReturnsTrue_ForNotAnalyzedEpisodes()
    {
        var episode = new QueuedEpisode();
        episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NotAnalyzed);

        Assert.True(BaseItemAnalyzerTask.HasUncachedAnalysisWork([episode], AnalysisMode.Introduction));
    }

    [Fact]
    public void HasUncachedAnalysisWork_ReturnsFalse_ForSettledNoSegmentsEpisodes()
    {
        // NoSegments is a negative-cache result for the current configuration. The season pass is
        // skipped here and only reopened once an episode is reset to NotAnalyzed (new episode,
        // config/Chromaprint-availability change, or settled-season reanalysis).
        var episode = new QueuedEpisode();
        episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NoSegments);

        Assert.False(BaseItemAnalyzerTask.HasUncachedAnalysisWork([episode], AnalysisMode.Introduction));
    }

    [Fact]
    public void HasUncachedAnalysisWork_ReturnsTrue_WhenAnyEpisodeNotAnalyzed()
    {
        // A freshly added (NotAnalyzed) episode reopens the whole season pass; the analyzers then
        // give the settled NoSegments episodes another chance via NeedsAnalysis().
        var settled = new QueuedEpisode();
        settled.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NoSegments);
        var fresh = new QueuedEpisode();
        fresh.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NotAnalyzed);

        Assert.True(BaseItemAnalyzerTask.HasUncachedAnalysisWork([settled, fresh], AnalysisMode.Introduction));
    }

    [Fact]
    public void HasUncachedAnalysisWork_ReturnsFalse_WhenAllEpisodesAreHandled()
    {
        var analyzed = new QueuedEpisode();
        analyzed.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.Analyzed);
        var userProvided = new QueuedEpisode();
        userProvided.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.UserProvided);

        Assert.False(BaseItemAnalyzerTask.HasUncachedAnalysisWork([analyzed, userProvided], AnalysisMode.Introduction));
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

    private static DateTime SettledTime() => Now.AddHours(-(PluginConfiguration.DefaultSettledSeasonDelayHours + 1));

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
                DateAdded = newestAdded.AddHours(-i), // index 0 is the most recent → Max
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
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");
        var season = Guid.NewGuid();
        var firstFive = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var firstSix = firstFive.Append(Guid.NewGuid()).ToArray();
        var firstSeven = firstSix.Append(Guid.NewGuid()).ToArray();
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
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstFive));

            await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstFive);
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive)); // already done for this set
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, [.. firstFive.Reverse()])); // order-insensitive set match
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSix)); // grew → eligible again
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, replacementFive)); // same count, different membership
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstFive)); // unrecorded mode stays eligible

            await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], replacementFive);
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, replacementFive));
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive));

            await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstSix);
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive)); // shrank → eligible again
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSix));
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSeven));

            await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstFive);
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstFive)); // shrink was recorded
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSix));
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Introduction, firstSeven));

            await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Credits], firstFive);
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstFive));
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstSix));

            await database.RecordSettleReanalysisAsync(season, [AnalysisMode.Credits], firstSix);
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstFive));
            Assert.False(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstSix));
            Assert.True(await ShouldReanalyzeAsync(database, season, AnalysisMode.Credits, firstSeven));

            // A fresh facade over the same file simulates a plugin restart: the recorded
            // episode sets must be read back from the database.
            var reopenedDatabase = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);

            Assert.False(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Introduction, firstFive));
            Assert.True(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Introduction, firstSix));
            Assert.True(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Introduction, firstSeven));
            Assert.False(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Credits, firstSix));
            Assert.True(await ShouldReanalyzeAsync(reopenedDatabase, season, AnalysisMode.Credits, firstSeven));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }



    [Fact]
    public async Task ResetItemsForReanalysisAsync_DeletesAutomaticSegments_PreservesUserAndOtherModes()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var autoEpisode = Guid.NewGuid();
        var userEpisode = Guid.NewGuid();
        var mixedEpisode = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.MigrateAsync();

                // Automatic intro — should be deleted.
                db.Segments.Add(new DbSegment(
                    autoEpisode,
                    AnalysisMode.Introduction,
                    TickConversions.FromSeconds(0),
                    TickConversions.FromSeconds(30),
                    SegmentSource.Chapter));

                // An episode holding both an automatic and a user intro is UserProvided for
                // the mode: the analyzers skip it, so the reset must keep its automatic row
                // (nothing would regenerate it).
                db.Segments.Add(new DbSegment(
                    mixedEpisode,
                    AnalysisMode.Introduction,
                    TickConversions.FromSeconds(0),
                    TickConversions.FromSeconds(60),
                    SegmentSource.Chromaprint));
                db.Segments.Add(new DbSegment(
                    mixedEpisode,
                    AnalysisMode.Introduction,
                    TickConversions.FromSeconds(300),
                    TickConversions.FromSeconds(330),
                    SegmentSource.User));

                // Tombstoned (user-deleted) automatic intro of the reset mode — must survive
                // the reset so the deleted range stays gone after re-analysis.
                db.Segments.Add(new DbSegment(
                    autoEpisode,
                    AnalysisMode.Introduction,
                    TickConversions.FromSeconds(60),
                    TickConversions.FromSeconds(90),
                    SegmentSource.Chapter)
                {
                    State = SegmentState.Suppressed,
                });

                // User-provided intro — must be preserved.
                db.Segments.Add(new DbSegment(
                    userEpisode,
                    AnalysisMode.Introduction,
                    TickConversions.FromSeconds(0),
                    TickConversions.FromSeconds(30),
                    SegmentSource.User));

                // Automatic recap on the same episode — different mode, must be preserved.
                db.Segments.Add(new DbSegment(
                    autoEpisode,
                    AnalysisMode.Recap,
                    TickConversions.FromSeconds(40),
                    TickConversions.FromSeconds(60),
                    SegmentSource.Chapter));

                db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Chromaprint));
                db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Recap, AnalyzerAction.Default));
                db.AnalyzedItems.AddRange(
                    new DbAnalyzedItem(autoEpisode, AnalysisMode.Introduction, "hash"),
                    new DbAnalyzedItem(userEpisode, AnalysisMode.Introduction, "hash"),
                    new DbAnalyzedItem(mixedEpisode, AnalysisMode.Introduction, "hash"),
                    new DbAnalyzedItem(autoEpisode, AnalysisMode.Recap, "recap-hash"));

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

            await DatabaseTestHelpers.CreateSegmentDatabase(dbPath).ResetItemsForReanalysisAsync(
                new[] { autoEpisode, userEpisode, mixedEpisode },
                new[] { AnalysisMode.Introduction });

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

                // The intro records are gone (every item is NotAnalyzed again); the season's
                // action and the other mode's record survive.
                Assert.False(db.AnalyzedItems.Any(a => a.Type == AnalysisMode.Introduction));
                var state = await db.SeasonStates.SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
                Assert.Equal(AnalyzerAction.Chromaprint, state.Action);

                var recap = await db.AnalyzedItems.SingleAsync(a => a.Type == AnalysisMode.Recap);
                Assert.Equal(autoEpisode, recap.ItemId);
                Assert.Equal("recap-hash", recap.ConfigHash);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ResetItemsForReanalysisAsync_DeletesCreditsDerivedAutomaticPreview()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var autoEpisode = Guid.NewGuid();
        var userPreviewEpisode = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.MigrateAsync();

                db.Segments.Add(new DbSegment(
                    autoEpisode,
                    AnalysisMode.Credits,
                    TickConversions.FromSeconds(1000),
                    TickConversions.FromSeconds(1100),
                    SegmentSource.Chapter));
                db.Segments.Add(new DbSegment(
                    autoEpisode,
                    AnalysisMode.Preview,
                    TickConversions.FromSeconds(1100),
                    TickConversions.FromSeconds(1320),
                    SegmentSource.CreditsDerived));
                db.Segments.Add(new DbSegment(
                    userPreviewEpisode,
                    AnalysisMode.Preview,
                    TickConversions.FromSeconds(1100),
                    TickConversions.FromSeconds(1320),
                    SegmentSource.User));

                db.AnalyzedItems.AddRange(
                    new DbAnalyzedItem(autoEpisode, AnalysisMode.Credits, "credits-hash"),
                    new DbAnalyzedItem(userPreviewEpisode, AnalysisMode.Credits, "credits-hash"),
                    new DbAnalyzedItem(autoEpisode, AnalysisMode.Preview, "preview-hash"),
                    new DbAnalyzedItem(userPreviewEpisode, AnalysisMode.Preview, "preview-hash"));

                await db.SaveChangesAsync();
            }

            var resetModes = BaseItemAnalyzerTask.ExpandSettledResetModesForDerivedSegments([AnalysisMode.Credits], true);

            await DatabaseTestHelpers.CreateSegmentDatabase(dbPath).ResetItemsForReanalysisAsync(
                new[] { autoEpisode, userPreviewEpisode },
                resetModes);

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.False(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Credits));
                Assert.False(db.Segments.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Preview));
                Assert.True(db.Segments.Any(s => s.ItemId == userPreviewEpisode && s.Type == AnalysisMode.Preview && s.Source == SegmentSource.User));
                Assert.False(db.AnalyzedItems.Any());
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task SeasonStateAndAnalysisRecords_UpsertWithoutClobberingEachOther()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

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
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task VerifyQueueAsync_ReopensNoSegments_WhenChromaprintBecomesAvailable()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");
        var mediaPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".mkv");

        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var config = new PluginConfiguration();
        var withoutChromaprintHash = ConfigHasher.Analysis(
            config,
            AnalysisMode.Introduction,
            AnalyzerAction.Default,
            ffmpegValid: false);

        try
        {
            await File.WriteAllTextAsync(mediaPath, string.Empty);

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.MigrateAsync();
                db.SeasonStates.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default));
                db.AnalyzedItems.Add(new DbAnalyzedItem(episodeId, AnalysisMode.Introduction, withoutChromaprintHash));
                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", config);

                var episode = new Episode();
                EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
                EntrypointTestHelpers.SetPropertyOrField(episode, "Path", mediaPath);
                var libraryManager = EntrypointTestHelpers.CreateLibraryManager(episode);
                EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", libraryManager);

                var unavailableQueueManager = new QueueManager(
                    NullLogger<QueueManager>.Instance,
                    libraryManager,
                    providerManager: null!,
                    fileSystem: null!,
                    ffmpegService: new FakeFfmpegService(ffmpegValid: false),
                    database: DatabaseTestHelpers.CreateSegmentDatabase(dbPath));
                var stillUnavailable = await unavailableQueueManager.VerifyQueueAsync(
                    [CreateQueuedEpisode(episodeId, seasonId)],
                    [AnalysisMode.Introduction]);
                Assert.Equal(EpisodeState.NoSegments, stillUnavailable.Single().GetAnalyzed(AnalysisMode.Introduction));

                var availableQueueManager = new QueueManager(
                    NullLogger<QueueManager>.Instance,
                    libraryManager,
                    providerManager: null!,
                    fileSystem: null!,
                    ffmpegService: new FakeFfmpegService(ffmpegValid: true),
                    database: DatabaseTestHelpers.CreateSegmentDatabase(dbPath));
                var reopened = await availableQueueManager.VerifyQueueAsync(
                    [CreateQueuedEpisode(episodeId, seasonId)],
                    [AnalysisMode.Introduction]);
                Assert.Equal(EpisodeState.NotAnalyzed, reopened.Single().GetAnalyzed(AnalysisMode.Introduction));
            }
        }
        finally
        {
            if (File.Exists(mediaPath))
            {
                File.Delete(mediaPath);
            }

            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task VerifyQueueAsync_StillAnalyzesDisabledItem()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");
        var mediaPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".mkv");

        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        try
        {
            await File.WriteAllTextAsync(mediaPath, string.Empty);

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.MigrateAsync();
                db.DisabledItems.Add(new DbDisabledItem(seasonId, episodeId));
                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration());

                var episode = new Episode();
                EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
                EntrypointTestHelpers.SetPropertyOrField(episode, "Path", mediaPath);
                var libraryManager = EntrypointTestHelpers.CreateLibraryManager(episode);
                EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", libraryManager);

                var queueManager = new QueueManager(
                    NullLogger<QueueManager>.Instance,
                    libraryManager,
                    providerManager: null!,
                    fileSystem: null!,
                    ffmpegService: new FakeFfmpegService(ffmpegValid: false),
                    database: DatabaseTestHelpers.CreateSegmentDatabase(dbPath));

                var verified = await queueManager.VerifyQueueAsync(
                    [CreateQueuedEpisode(episodeId, seasonId)],
                    [AnalysisMode.Introduction]);

                // The disable flag only withholds output from Jellyfin; the analysis
                // pipeline must keep processing the episode.
                Assert.Equal(episodeId, Assert.Single(verified).EpisodeId);
            }
        }
        finally
        {
            if (File.Exists(mediaPath))
            {
                File.Delete(mediaPath);
            }

            DeleteSqliteFiles(dbPath);
        }
    }

    private static QueuedEpisode CreateQueuedEpisode(Guid episodeId, Guid seasonId)
        => new()
        {
            EpisodeId = episodeId,
            SeasonId = seasonId,
            Name = "S01E01",
            SeriesName = "Rick and Morty",
        };

    private sealed class FakeFfmpegService(bool ffmpegValid) : IFFmpegService
    {
        public Task<bool> CheckFFmpegVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(ffmpegValid);

        public Task<uint[]> FingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<TimeRange[]> DetectSilenceAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<BlackFrame[]> DetectBlackFramesAsync(
            QueuedEpisode episode,
            TimeRange range,
            int minimum,
            int threshold,
            AnalysisMode mode,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<BlackFrame[]> DetectBlackFramesAsync(QueuedEpisode episode, int threshold, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<KeyframeVisual[]> DetectKeyframeVisualsAsync(QueuedEpisode episode, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<BlackInterval[]> DetectBlackIntervalsAsync(QueuedEpisode episode, TimeRange range, int threshold, int minimum, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<double[]> DetectKeyFramesAsync(QueuedEpisode episode, TimeRange range, AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<double?> ProbeAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public FFmpegCheckResult GetCheckResult() => FFmpegCheckResult.NotRun;
    }

    // Mirrors the production eligibility decision in BaseItemAnalyzerTask.GetSettleReanalysisModes,
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

    private static void DeleteSqliteFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
