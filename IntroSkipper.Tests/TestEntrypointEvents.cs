// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestEntrypointEvents
{
    [Fact]
    public void OnItemChanged_IgnoresImageUpdates()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", Guid.NewGuid());
        EntrypointTestHelpers.EnsureNonVirtual(movie);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: ItemUpdateType.ImageUpdate);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        var seasonsToAnalyze = EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint);
        Assert.Empty(seasonsToAnalyze);
    }

    [Fact]
    public void OnItemChanged_QueuesMovieId_WhenAutoDetectEnabled()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var movieId = Guid.NewGuid();
        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", movieId);
        EntrypointTestHelpers.EnsureNonVirtual(movie);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        var seasonsToAnalyze = EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint);
        Assert.Contains(movieId, seasonsToAnalyze);
    }

    [Fact]
    public void OnItemChanged_InvalidatesChangedEpisodeAnalysisAndCache()
    {
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var dbPath = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests", Guid.NewGuid() + ".db");
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var episode = new Episode();
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", itemId);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeasonId", seasonId);
        EntrypointTestHelpers.EnsureNonVirtual(episode);

        try
        {
            using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
            {
                var plugin = Plugin.Instance!;
                EntrypointTestHelpers.SetPrivateField(plugin, "_dbPath", dbPath);

                using (var db = Plugin.CreateDbContext())
                {
                    db.Database.EnsureCreated();
                    db.DbSegment.Add(new DbSegment(new Segment(itemId, new TimeRange(0, 30)), AnalysisMode.Introduction));
                    db.DbSegment.Add(new DbSegment(new Segment(itemId, new TimeRange(120, 150)), AnalysisMode.Credits, isUserProvided: true));
                    db.DbSeasonState.Add(new DbSeasonState(seasonId, AnalysisMode.Introduction, AnalyzerAction.Default, [itemId]));
                    db.DbSeasonState.Add(new DbSeasonState(seasonId, AnalysisMode.Credits, AnalyzerAction.Default, [itemId]));
                    db.SaveChanges();
                }

                using (var cacheDb = Plugin.CreateCacheDbContext())
                {
                    cacheDb.DetectionCache.Add(new DbDetectionCache(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
                    cacheDb.DetectionCache.Add(new DbDetectionCache(otherId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, EntrypointTestHelpers.EmptyJsonArray));
                    cacheDb.SaveChanges();
                }

                var args = EntrypointTestHelpers.CreateItemChangeEventArgs(episode, ItemUpdateType.None);
                EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

                using (var db = Plugin.CreateDbContext())
                {
                    Assert.False(db.DbSegment.Any(s => s.ItemId == itemId && !s.IsUserProvided));
                    Assert.True(db.DbSegment.Any(s => s.ItemId == itemId && s.IsUserProvided));
                    Assert.Empty(db.DbSeasonState.Single(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction).EpisodeIds);
                    Assert.Empty(db.DbSeasonState.Single(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Credits).EpisodeIds);
                }

                using var verificationCache = Plugin.CreateCacheDbContext();
                Assert.False(verificationCache.DetectionCache.Any(e => e.ItemId == itemId));
                Assert.True(verificationCache.DetectionCache.Any(e => e.ItemId == otherId));
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = dbPath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void OnItemChanged_DoesNothing_WhenAutoDetectDisabled()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: false);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", Guid.NewGuid());
        EntrypointTestHelpers.EnsureNonVirtual(movie);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        var seasonsToAnalyze = EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint);
        Assert.Empty(seasonsToAnalyze);
    }

    [Fact]
    public void OnItemRemoved_DeletesCache_ForEpisode()
    {
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var episodeId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var episode = new Episode();
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
        EntrypointTestHelpers.EnsureNonVirtual(episode);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: episode, updateReason: 0);
        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            using (var db = Plugin.CreateCacheDbContext())
            {
                db.DetectionCache.Add(new DbDetectionCache(
                    episodeId,
                    AnalysisMode.Introduction,
                    CacheEntryType.Chromaprint,
                    EntrypointTestHelpers.EmptyJsonArray));
                db.DetectionCache.Add(new DbDetectionCache(
                    episodeId,
                    AnalysisMode.Credits,
                    CacheEntryType.BlackFrame,
                    EntrypointTestHelpers.EmptyJsonArray,
                    100.5,
                    0));
                db.DetectionCache.Add(new DbDetectionCache(
                    otherId,
                    AnalysisMode.Introduction,
                    CacheEntryType.Chromaprint,
                    EntrypointTestHelpers.EmptyJsonArray));
                db.SaveChanges();
            }

            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);

            using var verificationDb = Plugin.CreateCacheDbContext();
            Assert.False(verificationDb.DetectionCache.Any(e => e.ItemId == episodeId));
            Assert.True(verificationDb.DetectionCache.Any(e => e.ItemId == otherId));
        }
    }

    [Fact]
    public void OnSettingsChanged_UpdatesConfig_AndSetsAnalyzeAgain()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: false);

        var newConfig = new PluginConfiguration { AutoDetectIntros = true };

        using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
        {
            // Ensure AnalyzeAgain starts false.
            var plugin = Plugin.Instance!;
            plugin.AnalyzeAgain = false;

            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnSettingsChanged", (BasePluginConfiguration)newConfig);

            Assert.True(plugin.AnalyzeAgain);
        }

        var storedConfig = (PluginConfiguration)EntrypointTestHelpers.GetPrivateField(entrypoint, "_config");
        Assert.Same(newConfig, storedConfig);
    }

    [Fact]
    public void OnLibraryRefresh_DoesNotSetAnalyzeAgain_WhenAutomaticTaskRunning()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);
        EntrypointTestHelpers.SetPrivateField(entrypoint, "_analyzeAgain", false);

        var cts = new System.Threading.CancellationTokenSource();
        EntrypointTestHelpers.SetPrivateStaticField(typeof(Entrypoint), "_cancellationTokenSource", cts);

        try
        {
            var taskResult = EntrypointTestHelpers.CreateTaskResult("RefreshLibrary", TaskCompletionStatus.Completed);
            var args = EntrypointTestHelpers.CreateTaskCompletionEventArgs(taskResult);

            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnLibraryRefresh", args);

            Assert.False((bool)EntrypointTestHelpers.GetPrivateField(entrypoint, "_analyzeAgain"));
        }
        finally
        {
            cts.Dispose();
            EntrypointTestHelpers.SetPrivateStaticField(typeof(Entrypoint), "_cancellationTokenSource", null);
        }
    }
}

public sealed class TestFingerprintCacheDeletionOnRemove
{
    [Fact]
    public void DeletesFingerprintCache_OnMovieRemoval_WhenAutoDetectEnabled()
    {
        var removedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", removedId);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Path", "C:\\IntroSkipper.Tests\\removed.mkv");
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        Assert.Equal(removedId, movie.Id);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            using (var db = Plugin.CreateCacheDbContext())
            {
                db.DetectionCache.Add(new DbDetectionCache(
                    removedId,
                    AnalysisMode.Introduction,
                    CacheEntryType.Chromaprint,
                    EntrypointTestHelpers.EmptyJsonArray));
                db.DetectionCache.Add(new DbDetectionCache(
                    removedId,
                    AnalysisMode.Credits,
                    CacheEntryType.BlackFrame,
                    EntrypointTestHelpers.EmptyJsonArray,
                    100.5,
                    0));
                db.DetectionCache.Add(new DbDetectionCache(
                    otherId,
                    AnalysisMode.Introduction,
                    CacheEntryType.Chromaprint,
                    EntrypointTestHelpers.EmptyJsonArray));
                db.SaveChanges();
            }

            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);

            using var verificationDb = Plugin.CreateCacheDbContext();
            Assert.False(verificationDb.DetectionCache.Any(e => e.ItemId == removedId));
            Assert.True(verificationDb.DetectionCache.Any(e => e.ItemId == otherId));
        }
    }

    [Fact]
    public void DoesNotDeleteFingerprintCache_OnMovieRemoval_WhenAutoDetectDisabled()
    {
        var removedId = Guid.NewGuid();
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: false);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", removedId);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Path", "C:\\IntroSkipper.Tests\\removed.mkv");
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        Assert.Equal(removedId, movie.Id);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            using (var db = Plugin.CreateCacheDbContext())
            {
                db.DetectionCache.Add(new DbDetectionCache(
                    removedId,
                    AnalysisMode.Introduction,
                    CacheEntryType.Chromaprint,
                    EntrypointTestHelpers.EmptyJsonArray));
                db.SaveChanges();
            }

            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);

            using var verificationDb = Plugin.CreateCacheDbContext();
            Assert.True(verificationDb.DetectionCache.Any(e => e.ItemId == removedId));
        }
    }

    [Fact]
    public void DoesNotDeleteFingerprintCache_WhenIdIsEmpty()
    {
        var removedId = Guid.Empty;

        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();

        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", removedId);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Path", "C:\\IntroSkipper.Tests\\removed.mkv");
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        Assert.Equal(removedId, movie.Id);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            using (var db = Plugin.CreateCacheDbContext())
            {
                db.DetectionCache.Add(new DbDetectionCache(
                    removedId,
                    AnalysisMode.Introduction,
                    CacheEntryType.Chromaprint,
                    EntrypointTestHelpers.EmptyJsonArray));
                db.SaveChanges();
            }

            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);

            using var verificationDb = Plugin.CreateCacheDbContext();
            Assert.True(verificationDb.DetectionCache.Any(e => e.ItemId == removedId));
        }
    }
}

