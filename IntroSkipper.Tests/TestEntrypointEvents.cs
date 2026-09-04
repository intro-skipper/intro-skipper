// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestEntrypointEvents
{
    [Fact]
    public void OnItemChanged_IgnoresImageUpdates()
    {
        using var scope = CreateScope(autoDetectIntros: true);
        using var entrypoint = EntrypointTestHelpers.CreateEntrypoint();

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(CreateMovie(Guid.NewGuid()), ItemUpdateType.ImageUpdate);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        Assert.Empty(EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint));
    }

    [Fact]
    public void OnItemChanged_QueuesMovieId_WhenAutoDetectEnabled()
    {
        using var scope = CreateScope(autoDetectIntros: true);
        using var entrypoint = EntrypointTestHelpers.CreateEntrypoint();
        var movieId = Guid.NewGuid();

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(CreateMovie(movieId), ItemUpdateType.None);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        Assert.Contains(movieId, EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint));
    }

    [Fact]
    public void OnItemChanged_QueuesChangedEpisodeForCoordinatedInvalidation()
    {
        using var scope = CreateScope(autoDetectIntros: true);
        using var entrypoint = EntrypointTestHelpers.CreateEntrypoint();
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episode = new Episode();
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", itemId);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeasonId", seasonId);
        EntrypointTestHelpers.EnsureNonVirtual(episode);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(episode, ItemUpdateType.None);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        Assert.Equal(seasonId, EntrypointTestHelpers.GetItemsToReset(entrypoint)[itemId]);
        Assert.Contains(seasonId, EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint));
    }

    [Fact]
    public void OnItemChanged_DoesNothing_WhenAutoDetectDisabled()
    {
        using var scope = CreateScope(autoDetectIntros: false);
        using var entrypoint = EntrypointTestHelpers.CreateEntrypoint();

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(CreateMovie(Guid.NewGuid()), ItemUpdateType.None);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        Assert.Empty(EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void OnItemRemoved_DeletesCacheRows_OnlyWithAutoDetectAndARealId(bool autoDetectIntros, bool emptyId, bool expectDeleted)
    {
        var removedId = emptyId ? Guid.Empty : Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var cacheDbPath = DatabaseTestHelpers.CreateTempCacheDbPath();
        using var scope = CreateScope(autoDetectIntros, cacheDbPath);
        using var entrypoint = EntrypointTestHelpers.CreateEntrypoint(cacheDbPath: cacheDbPath);
        SeedCacheRows(cacheDbPath, removedId, otherId);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(CreateMovie(removedId), ItemUpdateType.None);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);

        using var db = DatabaseTestHelpers.CreateCacheContext(cacheDbPath);
        Assert.Equal(!expectDeleted, db.DetectionCache.Any(e => e.ItemId == removedId));
        Assert.True(db.DetectionCache.Any(e => e.ItemId == otherId));
    }

    private static EntrypointTestHelpers.PluginInstanceScope CreateScope(bool autoDetectIntros, string? cacheDbPath = null)
        => EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AutoDetectIntros = autoDetectIntros }, cacheDbPath);

    private static Movie CreateMovie(Guid id)
    {
        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", id);
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        return movie;
    }

    private static void SeedCacheRows(string cacheDbPath, params Guid[] itemIds)
    {
        var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
        foreach (var itemId in itemIds)
        {
            cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0, EntrypointTestHelpers.EmptyJsonArray, string.Empty);
        }
    }
}
