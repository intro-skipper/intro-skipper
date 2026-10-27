// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using MediaBrowser.Controller.Library;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// The library watcher, driven through the library events it subscribes to. Its queue is
/// not started, so every item it hands over stays pending in the queue's status.
/// </summary>
public sealed class TestEntrypointEvents
{
    [Fact]
    public async Task ItemAdded_QueuesTheItemAsChanged()
    {
        using var scope = CreateScope(autoDetectIntros: true);
        var (entrypoint, queue, library) = EntrypointTestHelpers.CreateEntrypoint();
        await entrypoint.StartAsync(CancellationToken.None);

        library.RaiseItemAdded(JellyfinItems.Movie(Guid.NewGuid()));

        Assert.Equal(1, queue.Status.ChangedItems);
    }

    [Fact]
    public async Task ItemUpdated_QueuesAnEpisodeByItsOwnId_AndIgnoresImageUpdates()
    {
        using var scope = CreateScope(autoDetectIntros: true);
        var (entrypoint, queue, library) = EntrypointTestHelpers.CreateEntrypoint();
        await entrypoint.StartAsync(CancellationToken.None);
        var episode = JellyfinItems.Episode(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        library.RaiseItemUpdated(episode, ItemUpdateType.ImageUpdate);
        Assert.Equal(0, queue.Status.ChangedItems);

        // The pass resolves the episode's season, the host season for an in-season
        // special, when it starts rather than on the library event thread; the same
        // episode reported twice is one pending item.
        library.RaiseItemUpdated(episode);
        library.RaiseItemUpdated(episode);
        Assert.Equal(1, queue.Status.ChangedItems);
    }

    [Fact]
    public async Task ItemAdded_DoesNothing_WhenAutoDetectIsDisabled()
    {
        using var scope = CreateScope(autoDetectIntros: false);
        var (entrypoint, queue, library) = EntrypointTestHelpers.CreateEntrypoint();
        await entrypoint.StartAsync(CancellationToken.None);

        library.RaiseItemAdded(JellyfinItems.Movie(Guid.NewGuid()));

        Assert.Equal(0, queue.Status.ChangedItems);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task ItemRemoved_DeletesCacheRows_OnlyWithAutoDetectAndARealId(bool autoDetectIntros, bool emptyId, bool expectDeleted)
    {
        var removedId = emptyId ? Guid.Empty : Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var cacheDbPath = DatabaseTestHelpers.CreateTempCacheDbPath();
        using var scope = CreateScope(autoDetectIntros, cacheDbPath);
        var (entrypoint, _, library) = EntrypointTestHelpers.CreateEntrypoint(cacheDbPath: cacheDbPath);
        await entrypoint.StartAsync(CancellationToken.None);
        SeedCacheRows(cacheDbPath, removedId, otherId);

        library.RaiseItemRemoved(JellyfinItems.Movie(removedId));

        var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
        Assert.Equal(!expectDeleted, cacheDatabase.FindEntry(removedId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0) is not null);
        Assert.NotNull(cacheDatabase.FindEntry(otherId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0));
    }

    private static EntrypointTestHelpers.PluginInstanceScope CreateScope(bool autoDetectIntros, string? cacheDbPath = null)
        => EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AutoDetectIntros = autoDetectIntros }, cacheDbPath);

    private static void SeedCacheRows(string cacheDbPath, params Guid[] itemIds)
    {
        var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
        foreach (var itemId in itemIds)
        {
            cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0, EntrypointTestHelpers.EmptyJsonArray, string.Empty);
        }
    }
}
