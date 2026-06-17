// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TestQueueManager
{
    [Theory]
    // Directory prefix match (the primary use case: exclude a remote/cloud mount).
    [InlineData("/mnt/rd/Series/Season 01/S01E01.mkv", "/mnt/rd", true)]
    [InlineData("/media/local/Series/Season 01/S01E01.mkv", "/mnt/rd", false)]
    // Matching is case-insensitive in both directions.
    [InlineData("/mnt/RD/Series/S01E01.mkv", "/mnt/rd", true)]
    [InlineData("/mnt/rd/Series/S01E01.mkv", "/MNT/RD", true)]
    // A bare directory name anywhere in the path is enough.
    [InlineData("/media/zurg/Movies/Film (2020).mkv", "zurg", true)]
    [InlineData("C:\\Media\\Real-Debrid\\Show\\S01E01.mkv", "Real-Debrid", true)]
    // Windows-style separators are matched verbatim.
    [InlineData("C:\\Media\\Local\\Show\\S01E01.mkv", "\\Media\\Remote\\", false)]
    public void IsPathExcluded_SingleFragment_MatchesExpected(string path, string fragment, bool expected)
    {
        Assert.Equal(expected, QueueManager.IsPathExcluded(path, new[] { fragment }));
    }

    [Fact]
    public void IsPathExcluded_MatchesAnyConfiguredFragment()
    {
        string[] fragments = ["/mnt/rd", "/media/zurg", "Real-Debrid"];

        Assert.True(QueueManager.IsPathExcluded("/media/zurg/Movies/Film.mkv", fragments));
        Assert.True(QueueManager.IsPathExcluded("/mnt/rd/Show/S01E01.mkv", fragments));
        Assert.False(QueueManager.IsPathExcluded("/media/local/Show/S01E01.mkv", fragments));
    }

    [Fact]
    public void IsPathExcluded_EmptyPath_ReturnsFalse()
    {
        Assert.False(QueueManager.IsPathExcluded(string.Empty, new[] { "/mnt/rd" }));
    }

    [Fact]
    public void IsPathExcluded_NoFragments_ReturnsFalse()
    {
        Assert.False(QueueManager.IsPathExcluded("/mnt/rd/Show/S01E01.mkv", Array.Empty<string>()));
    }

    [Fact]
    public void IsPathExcluded_EmptyFragmentNeverMatches()
    {
        // A stray empty fragment must not cause every path to be excluded.
        Assert.False(QueueManager.IsPathExcluded("/media/local/Show/S01E01.mkv", new[] { string.Empty }));
    }

    [Fact]
    public void CreateExcludedPathList_QuotedCommaKeepsConfiguredPathWhole()
    {
        var fragments = QueueManager.CreateExcludedPathList("\"D:\\Media, Archive\", /mnt/rd");

        Assert.Equal(["D:\\Media, Archive", "/mnt/rd"], fragments);
        Assert.True(QueueManager.IsPathExcluded("D:\\Media, Archive\\Film.mkv", fragments));
        Assert.False(QueueManager.IsPathExcluded("D:\\Media\\Film.mkv", fragments));
        Assert.False(QueueManager.IsPathExcluded("D:\\Archive\\Film.mkv", fragments));
    }

    [Fact]
    public void CreateExcludedPathList_UnquotedQuoteStaysLiteral()
    {
        var fragments = QueueManager.CreateExcludedPathList("D:\\Media \"Archive\", /mnt/rd");

        Assert.Equal(["D:\\Media \"Archive\"", "/mnt/rd"], fragments);
    }

    [Fact]
    public void IsNameExcluded_UnquotedQuoteStaysLiteral()
    {
        var excludedNames = QueueManager.CreateExcludedNameSet("The \"Office\"");

        Assert.True(QueueManager.IsNameExcluded("The \"Office\"", excludedNames));
    }

    [Theory]
    [InlineData("My.Show", "my show", true)]
    [InlineData("Mob Psycho 100", "mob-psycho 100", true)]
    [InlineData("Other Show", "my show", false)]
    public void IsNameExcluded_NormalizesConfiguredNames(string configuredSeries, string candidateSeries, bool expected)
    {
        var excludedNames = QueueManager.CreateExcludedNameSet(configuredSeries);

        Assert.Equal(expected, QueueManager.IsNameExcluded(candidateSeries, excludedNames));
    }

    [Fact]
    public void IsNameExcluded_QuotedCommaKeepsConfiguredTitleWhole()
    {
        var excludedNames = QueueManager.CreateExcludedNameSet("\"Food, Inc.\", The.Office");

        Assert.True(QueueManager.IsNameExcluded("Food, Inc.", excludedNames));
        Assert.False(QueueManager.IsNameExcluded("Food", excludedNames));
        Assert.False(QueueManager.IsNameExcluded("Inc.", excludedNames));
        Assert.True(QueueManager.IsNameExcluded("The Office", excludedNames));
    }

    [Fact]
    public async Task VerifyQueueAsync_SkipsExcludedResolvedPathAndUpdatesVerifiedPath()
    {
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var tempRoot = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "exclude-paths", Guid.NewGuid().ToString("N"));
        var excludedDir = Path.Join(tempRoot, "remote");
        Directory.CreateDirectory(excludedDir);
        var excludedMediaPath = Path.Join(excludedDir, "S01E01.mkv");
        var includedDir = Path.Join(tempRoot, "local");
        Directory.CreateDirectory(includedDir);
        var includedMediaPath = Path.Join(includedDir, "S01E02.mkv");
        await File.WriteAllTextAsync(excludedMediaPath, string.Empty);
        await File.WriteAllTextAsync(includedMediaPath, string.Empty);

        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            var dbPath = Path.Join(tempRoot, "introskipper.db");
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var excludedMovie = new Movie();
            EntrypointTestHelpers.SetPropertyOrField(excludedMovie, "Id", itemId);
            EntrypointTestHelpers.SetPropertyOrField(excludedMovie, "Path", excludedMediaPath);
            EntrypointTestHelpers.EnsureNonVirtual(excludedMovie);

            var includedItemId = Guid.NewGuid();
            var includedMovie = new Movie();
            EntrypointTestHelpers.SetPropertyOrField(includedMovie, "Id", includedItemId);
            EntrypointTestHelpers.SetPropertyOrField(includedMovie, "Path", includedMediaPath);
            EntrypointTestHelpers.EnsureNonVirtual(includedMovie);

            var libraryManager = EntrypointTestHelpers.CreateLibraryManager(excludedMovie, includedMovie);
            var plugin = Plugin.Instance!;
            EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);
            EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", libraryManager);

            var queueManager = new QueueManager(NullLogger<QueueManager>.Instance, libraryManager, null!, null!, null!);
            EntrypointTestHelpers.SetPrivateField(queueManager, "_excludePaths", new[] { excludedDir });

            var verification = await queueManager.VerifyQueueAsync(
                seasonId,
                [
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = itemId, Name = "Episode 1", Path = "old-excluded-path.mkv" },
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = includedItemId, Name = "Episode 2", Path = "old-included-path.mkv" }
                ],
                [AnalysisMode.Introduction]);

            var candidate = Assert.Single(verification.Episodes);
            Assert.Equal(1, verification.SkippedCount);
            Assert.Equal(includedMediaPath, candidate.Path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyQueueAsync_SkipsExcludedSeriesAndReportsSkippedCount()
    {
        var excludedItemId = Guid.NewGuid();
        var includedItemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var tempRoot = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "exclude-series", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var excludedMediaPath = Path.Join(tempRoot, "S01E01.mkv");
        var includedMediaPath = Path.Join(tempRoot, "S01E02.mkv");
        await File.WriteAllTextAsync(excludedMediaPath, string.Empty);
        await File.WriteAllTextAsync(includedMediaPath, string.Empty);

        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            var dbPath = Path.Join(tempRoot, "introskipper.db");
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var libraryManager = EntrypointTestHelpers.CreateLibraryManager(
                CreateMovie(excludedItemId, excludedMediaPath),
                CreateMovie(includedItemId, includedMediaPath));
            var plugin = Plugin.Instance!;
            EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);
            EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", libraryManager);

            var queueManager = new QueueManager(NullLogger<QueueManager>.Instance, libraryManager, null!, null!, null!);
            EntrypointTestHelpers.SetPrivateField(queueManager, "_excludedSeriesNames", QueueManager.CreateExcludedNameSet("The.Office"));

            var verification = await queueManager.VerifyQueueAsync(
                seasonId,
                [
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = excludedItemId, SeriesName = "The Office", Name = "Episode 1", Path = "old-1.mkv" },
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = includedItemId, SeriesName = "Other Show", Name = "Episode 2", Path = "old-2.mkv" }
                ],
                [AnalysisMode.Introduction]);

            var candidate = Assert.Single(verification.Episodes);
            Assert.Equal(includedItemId, candidate.EpisodeId);
            Assert.Equal(1, verification.SkippedCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyQueueAsync_SkipsExcludedMovieAndReportsSkippedCount()
    {
        var excludedItemId = Guid.NewGuid();
        var includedItemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var tempRoot = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "exclude-movies", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var excludedMediaPath = Path.Join(tempRoot, "movie-1.mkv");
        var includedMediaPath = Path.Join(tempRoot, "movie-2.mkv");
        await File.WriteAllTextAsync(excludedMediaPath, string.Empty);
        await File.WriteAllTextAsync(includedMediaPath, string.Empty);

        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            var dbPath = Path.Join(tempRoot, "introskipper.db");
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var libraryManager = EntrypointTestHelpers.CreateLibraryManager(
                CreateMovie(excludedItemId, excludedMediaPath),
                CreateMovie(includedItemId, includedMediaPath));
            var plugin = Plugin.Instance!;
            EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);
            EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", libraryManager);

            var queueManager = new QueueManager(NullLogger<QueueManager>.Instance, libraryManager, null!, null!, null!);
            EntrypointTestHelpers.SetPrivateField(queueManager, "_excludedMovieNames", QueueManager.CreateExcludedNameSet("The.Matrix"));

            var verification = await queueManager.VerifyQueueAsync(
                seasonId,
                [
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = excludedItemId, Category = QueuedMediaCategory.Movie, Name = "The Matrix", Path = "old-1.mkv" },
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = includedItemId, Category = QueuedMediaCategory.Movie, Name = "Other Movie", Path = "old-2.mkv" }
                ],
                [AnalysisMode.Introduction]);

            var candidate = Assert.Single(verification.Episodes);
            Assert.Equal(includedItemId, candidate.EpisodeId);
            Assert.Equal(1, verification.SkippedCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyQueueAsync_AppliesStoredStatesAfterPathVerification()
    {
        var seasonId = Guid.NewGuid();
        var analyzedItemId = Guid.NewGuid();
        var noSegmentsItemId = Guid.NewGuid();
        var userProvidedItemId = Guid.NewGuid();
        var tempRoot = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "queue-verification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var analyzedPath = Path.Join(tempRoot, "S01E01.mkv");
        var noSegmentsPath = Path.Join(tempRoot, "S01E02.mkv");
        var userProvidedPath = Path.Join(tempRoot, "S01E03.mkv");
        await File.WriteAllTextAsync(analyzedPath, string.Empty);
        await File.WriteAllTextAsync(noSegmentsPath, string.Empty);
        await File.WriteAllTextAsync(userProvidedPath, string.Empty);

        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            var dbPath = Path.Join(tempRoot, "introskipper.db");
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var libraryManager = EntrypointTestHelpers.CreateLibraryManager(
                CreateMovie(analyzedItemId, analyzedPath),
                CreateMovie(noSegmentsItemId, noSegmentsPath),
                CreateMovie(userProvidedItemId, userProvidedPath));
            var plugin = Plugin.Instance!;
            EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);
            EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", libraryManager);
            EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new IntroSkipper.Configuration.PluginConfiguration());

            var introHash = ConfigHasher.Analysis(plugin.Configuration, AnalysisMode.Introduction, AnalyzerAction.Default);
            await using (var db = new IntroSkipperDbContext(dbPath))
            {
                db.DbSeasonState.Add(new DbSeasonState(
                    seasonId,
                    AnalysisMode.Introduction,
                    AnalyzerAction.Default,
                    [analyzedItemId, noSegmentsItemId],
                    introHash));
                db.DbSegment.Add(new DbSegment(
                    new Segment(analyzedItemId, new TimeRange(10, 20)),
                    AnalysisMode.Introduction,
                    isUserProvided: false,
                    configHash: introHash));
                db.DbSegment.Add(new DbSegment(
                    new Segment(userProvidedItemId, new TimeRange(30, 40)),
                    AnalysisMode.Credits,
                    isUserProvided: true,
                    configHash: string.Empty));
                await db.SaveChangesAsync();
            }

            var queueManager = new QueueManager(NullLogger<QueueManager>.Instance, libraryManager, null!, null!, null!);
            var verification = await queueManager.VerifyQueueAsync(
                seasonId,
                [
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = analyzedItemId, Name = "Episode 1", Path = "old-1.mkv" },
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = noSegmentsItemId, Name = "Episode 2", Path = "old-2.mkv" },
                    new QueuedEpisode { SeasonId = seasonId, EpisodeId = userProvidedItemId, Name = "Episode 3", Path = "old-3.mkv" }
                ],
                [AnalysisMode.Introduction, AnalysisMode.Credits]);

            var verified = verification.Episodes;
            Assert.Equal(3, verified.Count);
            Assert.Equal(0, verification.SkippedCount);
            Assert.Equal(EpisodeState.Analyzed, verified.Single(e => e.EpisodeId == analyzedItemId).GetAnalyzed(AnalysisMode.Introduction));
            Assert.Equal(EpisodeState.NoSegments, verified.Single(e => e.EpisodeId == noSegmentsItemId).GetAnalyzed(AnalysisMode.Introduction));
            Assert.Equal(EpisodeState.UserProvided, verified.Single(e => e.EpisodeId == userProvidedItemId).GetAnalyzed(AnalysisMode.Credits));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static Movie CreateMovie(Guid id, string path)
    {
        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", id);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Path", path);
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        return movie;
    }
}
