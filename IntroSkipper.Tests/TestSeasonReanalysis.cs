// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestSeasonReanalysisPlanner
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
    public void ShouldAnalyzeItems_ReturnsTrue_ForNotAnalyzedEpisodes()
    {
        var episode = new QueuedEpisode();
        episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NotAnalyzed);

        Assert.True(BaseItemAnalyzerTask.ShouldAnalyzeItems([episode], AnalysisMode.Introduction));
    }

    [Fact]
    public void ShouldAnalyzeItems_ReturnsFalse_ForSettledNoSegmentsEpisodes()
    {
        // NoSegments is a negative-cache result for the current configuration. The season pass is
        // skipped here and only reopened once an episode is reset to NotAnalyzed (new episode,
        // config/Chromaprint-availability change, or settled-season reanalysis).
        var episode = new QueuedEpisode();
        episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NoSegments);

        Assert.False(BaseItemAnalyzerTask.ShouldAnalyzeItems([episode], AnalysisMode.Introduction));
    }

    [Fact]
    public void ShouldAnalyzeItems_ReturnsTrue_WhenAnyEpisodeNotAnalyzed()
    {
        // A freshly added (NotAnalyzed) episode reopens the whole season pass; the analyzers then
        // give the settled NoSegments episodes another chance via NeedsAnalysis().
        var settled = new QueuedEpisode();
        settled.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NoSegments);
        var fresh = new QueuedEpisode();
        fresh.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NotAnalyzed);

        Assert.True(BaseItemAnalyzerTask.ShouldAnalyzeItems([settled, fresh], AnalysisMode.Introduction));
    }

    [Fact]
    public void ShouldAnalyzeItems_ReturnsFalse_WhenAllEpisodesAreHandled()
    {
        var analyzed = new QueuedEpisode();
        analyzed.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.Analyzed);
        var userProvided = new QueuedEpisode();
        userProvided.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.UserProvided);

        Assert.False(BaseItemAnalyzerTask.ShouldAnalyzeItems([analyzed, userProvided], AnalysisMode.Introduction));
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
                await db.Database.EnsureCreatedAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                // Stays eligible until the work is explicitly recorded, so a failed reset is retried.
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstFive));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstFive));

                await Plugin.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstFive);
                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstFive)); // already done for this set
                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstFive.Reverse().ToArray())); // order-insensitive set match
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstSix)); // grew → eligible again
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, replacementFive)); // same count, different membership
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstFive)); // unrecorded mode stays eligible

                await Plugin.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], replacementFive);
                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, replacementFive));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstFive));

                await Plugin.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstSix);
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstFive)); // shrank → eligible again
                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstSix));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstSeven));

                await Plugin.RecordSettleReanalysisAsync(season, [AnalysisMode.Introduction], firstFive);
                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstFive)); // shrink was recorded
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstSix));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstSeven));

                await Plugin.RecordSettleReanalysisAsync(season, [AnalysisMode.Credits], firstFive);
                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstFive));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstSix));

                await Plugin.RecordSettleReanalysisAsync(season, [AnalysisMode.Credits], firstSix);
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstFive));
                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstSix));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstSeven));
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstFive));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstSix));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Introduction, firstSeven));
                Assert.False(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstSix));
                Assert.True(await ShouldReanalyzeAsync(season, AnalysisMode.Credits, firstSeven));
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }



    [Fact]
    public async Task ResetSeasonForReanalysisAsync_DeletesAutomaticSegments_PreservesUserAndOtherModes()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var autoEpisode = Guid.NewGuid();
        var userEpisode = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();

                // Automatic intro — should be deleted.
                db.DbSegment.Add(new DbSegment(
                    new Segment(autoEpisode, new TimeRange(0, 30)),
                    AnalysisMode.Introduction));

                // User-provided intro — must be preserved.
                db.DbSegment.Add(new DbSegment(
                    new Segment(userEpisode, new TimeRange(0, 30)),
                    AnalysisMode.Introduction,
                    isUserProvided: true));

                // Automatic recap on the same episode — different mode, must be preserved.
                db.DbSegment.Add(new DbSegment(
                    new Segment(autoEpisode, new TimeRange(40, 60)),
                    AnalysisMode.Recap));

                db.DbSeasonState.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Introduction,
                    AnalyzerAction.Chromaprint,
                    new[] { autoEpisode, userEpisode },
                    "hash"));

                db.DbSeasonState.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Recap,
                    AnalyzerAction.Default,
                    new[] { autoEpisode },
                    "recap-hash"));

                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                using (var cacheDb = Plugin.CreateCacheDbContext())
                {
                    cacheDb.DetectionCache.Add(new DbDetectionCache(
                        autoEpisode,
                        AnalysisMode.Introduction,
                        CacheEntryType.Chromaprint,
                        EntrypointTestHelpers.EmptyJsonArray));
                    await cacheDb.SaveChangesAsync();
                }

                await Plugin.ResetSeasonForReanalysisAsync(
                    seasonId,
                    new[] { autoEpisode, userEpisode },
                    new[] { AnalysisMode.Introduction });

                using (var cacheDb = Plugin.CreateCacheDbContext())
                {
                    Assert.True(cacheDb.DetectionCache.Any(e => e.ItemId == autoEpisode && e.Mode == AnalysisMode.Introduction));
                }
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.False(db.DbSegment.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Introduction));
                Assert.True(db.DbSegment.Any(s => s.ItemId == userEpisode && s.Type == AnalysisMode.Introduction && s.IsUserProvided));
                Assert.True(db.DbSegment.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Recap));

                var state = await db.DbSeasonState.SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
                Assert.Empty(state.EpisodeIds);
                Assert.Equal(AnalyzerAction.Chromaprint, state.Action);
                Assert.Equal("hash", state.ConfigHash);

                var recapState = await db.DbSeasonState.SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Recap);
                Assert.Equal(new[] { autoEpisode }, recapState.EpisodeIds);
                Assert.Equal(AnalyzerAction.Default, recapState.Action);
                Assert.Equal("recap-hash", recapState.ConfigHash);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ResetSeasonForReanalysisAsync_DeletesCreditsDerivedAutomaticPreview()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");

        var seasonId = Guid.NewGuid();
        var autoEpisode = Guid.NewGuid();
        var userPreviewEpisode = Guid.NewGuid();

        try
        {
            using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();

                db.DbSegment.Add(new DbSegment(
                    new Segment(autoEpisode, new TimeRange(1000, 1100)),
                    AnalysisMode.Credits));
                db.DbSegment.Add(new DbSegment(
                    new Segment(autoEpisode, new TimeRange(1100, 1320)),
                    AnalysisMode.Preview));
                db.DbSegment.Add(new DbSegment(
                    new Segment(userPreviewEpisode, new TimeRange(1100, 1320)),
                    AnalysisMode.Preview,
                    isUserProvided: true));

                db.DbSeasonState.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Credits,
                    AnalyzerAction.Default,
                    new[] { autoEpisode, userPreviewEpisode },
                    "credits-hash"));
                db.DbSeasonState.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Preview,
                    AnalyzerAction.Default,
                    new[] { autoEpisode, userPreviewEpisode },
                    "preview-hash"));

                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);
                var resetModes = BaseItemAnalyzerTask.ExpandSettledResetModesForDerivedSegments([AnalysisMode.Credits], true);

                await Plugin.ResetSeasonForReanalysisAsync(
                    seasonId,
                    new[] { autoEpisode, userPreviewEpisode },
                    resetModes);
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.False(db.DbSegment.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Credits));
                Assert.False(db.DbSegment.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Preview));
                Assert.True(db.DbSegment.Any(s => s.ItemId == userPreviewEpisode && s.Type == AnalysisMode.Preview && s.IsUserProvided));

                var creditsState = await db.DbSeasonState.SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Credits);
                var previewState = await db.DbSeasonState.SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Preview);
                Assert.Empty(creditsState.EpisodeIds);
                Assert.Empty(previewState.EpisodeIds);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task SeasonState_UpsertsActionsEpisodeIdsAndSettledReanalysisWithoutClobberingFields()
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
                await db.Database.EnsureCreatedAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                await Plugin.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, [episodeA, episodeB], "hash-a");
                await Plugin.SetAnalyzerActionAsync(
                    seasonId,
                    new Dictionary<AnalysisMode, AnalyzerAction>
                    {
                        [AnalysisMode.Introduction] = AnalyzerAction.Chromaprint,
                    });
                await Plugin.RecordSettleReanalysisAsync(seasonId, [AnalysisMode.Introduction], completedEpisodeIds);
                await Plugin.RecordSettleReanalysisAsync(seasonId, [AnalysisMode.Introduction], lowerEpisodeIds);
                await Plugin.RemoveEpisodeIdAsync(seasonId, AnalysisMode.Introduction, episodeA);
                await Plugin.RemoveEpisodeIdAsync(seasonId, AnalysisMode.Introduction, Guid.NewGuid());
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                var state = await db.DbSeasonState.SingleAsync();
                Assert.Equal(seasonId, state.SeasonId);
                Assert.Equal(AnalysisMode.Introduction, state.Type);
                Assert.Equal(AnalyzerAction.Chromaprint, state.Action);
                Assert.Equal(new[] { episodeB }, state.EpisodeIds);
                Assert.Equal("hash-a", state.ConfigHash);
                Assert.Equal(lowerEpisodeIds, state.SettledReanalysisEpisodeIds);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    // Mirrors the production eligibility decision in BaseItemAnalyzerTask.GetSettleReanalysisModesAsync,
    // exercising the same batch read (GetSettleReanalysisStatesAsync) and set comparison the analyzer uses.
    private static async Task<bool> ShouldReanalyzeAsync(
        Guid seasonId,
        AnalysisMode mode,
        IReadOnlyCollection<Guid> episodeIds)
    {
        var states = await Plugin.GetSettleReanalysisStatesAsync(seasonId);
        return !states.TryGetValue(mode, out var state)
            || Plugin.ShouldSettleReanalyze(state.SettledReanalysisEpisodeIds, episodeIds);
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
