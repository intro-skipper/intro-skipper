// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestQueueManager
{
    [Fact]
    public async Task GetMediaInventoryAsync_QueuesRegularEpisodeAndMovie_AndPublishesQueueState()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = InitializePlugin();

        var seriesId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var seasonId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var episodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var movieId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var episode = JellyfinItems.Episode(episodeId, seriesId, seasonId);
        var movie = JellyfinItems.Movie(movieId);

        var queueManager = CreateQueueManager(episode, movie);

        var queue = await queueManager.GetMediaInventoryAsync();

        var queuedEpisode = Assert.Single(queue[seasonId]);
        Assert.Equal(episodeId, queuedEpisode.EpisodeId);
        Assert.Equal(seriesId, queuedEpisode.SeriesId);
        Assert.Equal(seasonId, queuedEpisode.SeasonId);
        Assert.Equal("Pilot", queuedEpisode.Name);
        Assert.Equal("Series", queuedEpisode.SeriesName);
        Assert.Equal(QueuedMediaCategory.Episode, queuedEpisode.Category);
        Assert.Equal(240, queuedEpisode.Duration);
        Assert.False(queuedEpisode.IsExcluded);

        var queuedMovie = Assert.Single(queue[movieId]);
        Assert.Equal(movieId, queuedMovie.EpisodeId);
        Assert.Equal(movieId, queuedMovie.SeriesId);
        Assert.Equal(movieId, queuedMovie.SeasonId);
        Assert.Equal("Feature", queuedMovie.Name);
        Assert.Equal(QueuedMediaCategory.Movie, queuedMovie.Category);
        Assert.Equal(240, queuedMovie.Duration);
        Assert.False(queuedMovie.IsExcluded);

        Assert.Equal(2, plugin.TotalQueued);
        Assert.Equal(2, plugin.TotalSeasons);
        Assert.Equal(2, plugin.QueuedMediaItems.Count);
        Assert.Equal(episodeId, Assert.Single(plugin.QueuedMediaItems[seasonId]).EpisodeId);
        Assert.Equal(movieId, Assert.Single(plugin.QueuedMediaItems[movieId]).EpisodeId);
    }

    [Fact]
    public async Task GetMediaInventoryAsync_WithSeasonIds_MergesIntoPublishedQueueWithoutUnrelatedItems()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var existingSeasonId = Guid.NewGuid();
        var existingQueue = new ConcurrentDictionary<Guid, List<QueuedEpisode>>
        {
            [existingSeasonId] = [new QueuedEpisode { EpisodeId = Guid.NewGuid(), SeasonId = existingSeasonId }]
        };
        var plugin = InitializePlugin(existingQueue);

        var seriesId = Guid.NewGuid();
        var targetSeasonId = Guid.NewGuid();
        var otherSeasonId = Guid.NewGuid();
        var targetEpisode = JellyfinItems.Episode(Guid.NewGuid(), seriesId, targetSeasonId);
        var otherEpisode = JellyfinItems.Episode(Guid.NewGuid(), seriesId, otherSeasonId);
        var movie = JellyfinItems.Movie(Guid.NewGuid(), "Unrelated feature", "/media/unrelated.mkv");

        var queueManager = CreateQueueManager(targetEpisode, otherEpisode, movie);

        var queue = await queueManager.GetMediaInventoryAsync(seasonIds: [targetSeasonId]);

        var queuedEpisode = Assert.Single(queue[targetSeasonId]);
        Assert.Equal(targetEpisode.Id, queuedEpisode.EpisodeId);
        Assert.DoesNotContain(otherSeasonId, queue.Keys);
        Assert.DoesNotContain(movie.Id, queue.Keys);

        // The scoped result merges into the published queue instead of replacing it: the
        // pre-existing entry survives, the requested season becomes visible to the dashboard
        // endpoints, and unrelated items stay out.
        Assert.True(plugin.QueuedMediaItems.ContainsKey(existingSeasonId));
        Assert.Equal(targetEpisode.Id, Assert.Single(plugin.QueuedMediaItems[targetSeasonId]).EpisodeId);
        Assert.DoesNotContain(otherSeasonId, plugin.QueuedMediaItems.Keys);
        Assert.DoesNotContain(movie.Id, plugin.QueuedMediaItems.Keys);
        Assert.Equal(2, plugin.TotalQueued);
        Assert.Equal(2, plugin.TotalSeasons);
    }

    [Fact]
    public async Task GetMediaInventoryAsync_WithSeasonIds_QueriesOnlyLibrariesOwningARequestedItem()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        InitializePlugin();

        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episode = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId);
        var owningFolder = JellyfinItems.Folder("Media");
        var unrelatedFolder = JellyfinItems.Folder("Other");
        var libraryManager = EntrypointTestHelpers.FakeLibraryManager.Create(
            [owningFolder, unrelatedFolder],
            [episode],
            new Dictionary<Guid, Guid> { [seasonId] = Guid.Parse(owningFolder.ItemId!) });
        var queueManager = new QueueManager(
            NullLogger<QueueManager>.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        var queue = await queueManager.GetMediaInventoryAsync(seasonIds: [seasonId]);

        Assert.Equal(episode.Id, Assert.Single(queue[seasonId]).EpisodeId);

        var fake = (EntrypointTestHelpers.FakeLibraryManager)(object)libraryManager;
        Assert.Contains(Guid.Parse(owningFolder.ItemId!), fake.QueriedLibraryIds);
        Assert.DoesNotContain(Guid.Parse(unrelatedFolder.ItemId!), fake.QueriedLibraryIds);
    }

    [Fact]
    public async Task GetMediaInventoryAsync_WithSeasonIds_IncludesInSeasonSpecials()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        InitializePlugin();

        var seriesId = Guid.NewGuid();
        var hostSeasonId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var host = JellyfinItems.Episode(Guid.NewGuid(), seriesId, hostSeasonId);
        var special = JellyfinItems.Episode(Guid.NewGuid(), seriesId, specialsSeasonId);
        special.ParentIndexNumber = 0;
        special.AirsBeforeSeasonNumber = 1;
        special.Name = "Special";

        var queueManager = CreateQueueManager(host, special);

        var queue = await queueManager.GetMediaInventoryAsync(seasonIds: [hostSeasonId]);

        var season = Assert.Single(queue);
        Assert.Equal(hostSeasonId, season.Key);
        Assert.Equal(2, season.Value.Count);
        Assert.All(season.Value, episode => Assert.Equal(hostSeasonId, episode.SeasonId));
        Assert.Contains(season.Value, episode => episode.EpisodeId == special.Id);
    }

    private static Plugin InitializePlugin(ConcurrentDictionary<Guid, List<QueuedEpisode>>? queuedMediaItems = null)
    {
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration());
        EntrypointTestHelpers.SetPropertyOrField(
            plugin,
            "QueuedMediaItems",
            queuedMediaItems ?? new ConcurrentDictionary<Guid, List<QueuedEpisode>>());
        EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());
        return plugin;
    }

    private static QueueManager CreateQueueManager(params BaseItem[] items)
        => new(
            NullLogger<QueueManager>.Instance,
            EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], items),
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

}
