// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
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
    public void IsSettledForReanalysis_ReturnsFalse_WhenExcluded()
    {
        var config = new PluginConfiguration { ReanalyzeSettledSeasons = true };
        var season = Season(SeasonReanalysisPlanner.MinimumEpisodes, SettledTime(), excluded: true);

        Assert.False(SeasonReanalysisPlanner.IsSettledForReanalysis(season, config, Now));
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

    private static DateTime SettledTime() => Now.AddHours(-(SeasonReanalysisPlanner.SettleHours + 1));

    private static List<QueuedEpisode> Season(
        int count,
        DateTime newestCreated,
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
                DateCreated = newestCreated.AddHours(-i), // index 0 is the most recent → Max
            });
        }

        return episodes;
    }
}

public sealed class TestSeasonReanalysisReset
{
    [Fact]
    public void TryBeginSettleReanalysis_RunsOncePerEpisodeCount()
    {
        using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
        {
            var plugin = Plugin.Instance!;
            // PluginInstanceScope builds the plugin without running field initializers.
            EntrypointTestHelpers.SetPrivateField(plugin, "_settleReanalyzedCounts", new ConcurrentDictionary<Guid, int>());

            var season = Guid.NewGuid();

            Assert.True(plugin.TryBeginSettleReanalysis(season, 5));   // first sighting
            Assert.False(plugin.TryBeginSettleReanalysis(season, 5));  // already done at this count
            Assert.True(plugin.TryBeginSettleReanalysis(season, 6));   // grew → run again
            Assert.False(plugin.TryBeginSettleReanalysis(season, 6));
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

                db.DbSeasonInfo.Add(new DbSeasonInfo(
                    seasonId,
                    AnalysisMode.Introduction,
                    AnalyzerAction.Default,
                    new[] { autoEpisode, userEpisode },
                    "hash"));

                await db.SaveChangesAsync();
            }

            using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                await plugin.ResetSeasonForReanalysisAsync(
                    seasonId,
                    new[] { autoEpisode, userEpisode },
                    new[] { AnalysisMode.Introduction });
            }

            using (var db = new IntroSkipperDbContext(dbPath))
            {
                Assert.False(db.DbSegment.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Introduction));
                Assert.True(db.DbSegment.Any(s => s.ItemId == userEpisode && s.Type == AnalysisMode.Introduction && s.IsUserProvided));
                Assert.True(db.DbSegment.Any(s => s.ItemId == autoEpisode && s.Type == AnalysisMode.Recap));

                var info = await db.DbSeasonInfo.SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
                Assert.Empty(info.EpisodeIds);
            }
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
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
