// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestQueueManager
{
    [Fact]
    public async Task GetMediaItems_QueuesRegularEpisodeAndMovie_AndPublishesQueueState()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration());
        EntrypointTestHelpers.SetPropertyOrField(plugin, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());
        EntrypointTestHelpers.SetPrivateField(plugin, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());

        var seriesId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var seasonId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var episodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var movieId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var episode = CreateEpisode(episodeId, seriesId, seasonId);
        var movie = new Movie
        {
            Id = movieId,
            Name = "Feature",
            Path = "/media/feature.mkv",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };

        var queueManager = new QueueManager(
            NullLogger<QueueManager>.Instance,
            QueueLibraryManager.Create([NewFolder("Media")], [episode, movie]),
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        var queue = await queueManager.GetMediaItems();

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
    public async Task GetMediaItems_WithSeasonIds_DoesNotQueueUnrelatedItems()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var plugin = Plugin.Instance!;
        EntrypointTestHelpers.SetPropertyOrField(plugin, "Configuration", new PluginConfiguration());
        EntrypointTestHelpers.SetPropertyOrField(plugin, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());

        var seriesId = Guid.NewGuid();
        var targetSeasonId = Guid.NewGuid();
        var otherSeasonId = Guid.NewGuid();
        var targetEpisode = CreateEpisode(Guid.NewGuid(), seriesId, targetSeasonId);
        var otherEpisode = CreateEpisode(Guid.NewGuid(), seriesId, otherSeasonId);
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Unrelated feature",
            Path = "/media/unrelated.mkv",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };

        var queueManager = new QueueManager(
            NullLogger<QueueManager>.Instance,
            QueueLibraryManager.Create([NewFolder("Media")], [targetEpisode, otherEpisode, movie]),
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        var queue = await queueManager.GetMediaItems([targetSeasonId]);

        var queuedEpisode = Assert.Single(queue[targetSeasonId]);
        Assert.Equal(targetEpisode.Id, queuedEpisode.EpisodeId);
        Assert.DoesNotContain(otherSeasonId, queue.Keys);
        Assert.DoesNotContain(movie.Id, queue.Keys);
    }

    private static Episode CreateEpisode(Guid episodeId, Guid seriesId, Guid seasonId)
    {
        var episode = new Episode
        {
            Name = "Pilot",
            SeriesId = seriesId,
            SeasonId = seasonId,
            ParentIndexNumber = 1,
            IndexNumber = 1,
            Path = "/media/series/s01e01.mkv",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        };
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeriesName", "Series");
        EntrypointTestHelpers.EnsureNonVirtual(episode);
        return episode;
    }

    private static VirtualFolderInfo NewFolder(string name)
        => new()
        {
            Name = name,
            ItemId = Guid.Parse("55555555-5555-5555-5555-555555555555").ToString(),
        };

    private class QueueLibraryManager : DispatchProxy
    {
        private List<VirtualFolderInfo> _folders = [];
        private List<BaseItem> _items = [];

        public static ILibraryManager Create(List<VirtualFolderInfo> folders, List<BaseItem> items)
        {
            var proxy = Create<ILibraryManager, QueueLibraryManager>();
            var fake = (QueueLibraryManager)(object)proxy;
            fake._folders = folders;
            fake._items = items;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(ILibraryManager.GetVirtualFolders) => _folders,
                nameof(ILibraryManager.GetItemList) => GetItems(args),
                nameof(ILibraryManager.GetItemById) => null,
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
        }

        private List<BaseItem> GetItems(object?[]? args)
        {
            var query = args?.OfType<InternalItemsQuery>().SingleOrDefault();
            if (query?.AncestorIds is { Length: > 0 })
            {
                return _items
                    .Where(item => item is Episode episode && query.AncestorIds.Contains(episode.SeasonId))
                    .ToList();
            }

            if (query?.ItemIds is { Length: > 0 })
            {
                return _items.Where(item => query.ItemIds.Contains(item.Id)).ToList();
            }

            return new List<BaseItem>(_items);
        }
    }
}
