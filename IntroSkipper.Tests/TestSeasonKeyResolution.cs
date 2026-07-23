// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// In-season specials are grouped under the season they air within, so the analysis queue key
/// (and every season-state row keyed by it) differs from the special's own Jellyfin SeasonId.
/// These tests pin that the queued episode carries the resolved key and that the segment
/// editor resolves the same key when reopening analysis after a segment delete.
/// </summary>
public sealed class TestSeasonKeyResolution
{
    [Fact]
    public async Task GetMediaItems_AssignsResolvedSeasonKey_ToInSeasonSpecials()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager());

        var seriesId = Guid.NewGuid();
        var hostSeasonId = Guid.NewGuid();
        var specialsSeasonId = Guid.NewGuid();
        var host = CreateEpisode(Guid.NewGuid(), seriesId, hostSeasonId, parentIndexNumber: 1, "/media/show/s01e01.mkv");
        var special = CreateEpisode(Guid.NewGuid(), seriesId, specialsSeasonId, parentIndexNumber: 0, "/media/show/s00e01.mkv");
        EntrypointTestHelpers.SetPropertyOrField(special, "AirsBeforeSeasonNumber", (int?)1);

        var queueManager = new QueueManager(
            NullLogger<QueueManager>.Instance,
            FakeLibraryManager.Create([NewFolder("Shows")], [host, special]),
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            DatabaseTestHelpers.CreateTempSegmentDatabase());

        var queue = await queueManager.GetMediaItems(includeExcluded: true);

        var season = Assert.Single(queue);
        Assert.Equal(hostSeasonId, season.Key);
        Assert.Equal(2, season.Value.Count);
        Assert.All(season.Value, episode => Assert.Equal(hostSeasonId, episode.SeasonId));
    }

    [Fact]
    public async Task DeleteSegment_RemovesEpisodeFromHostSeasonState_ForInSeasonSpecial()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            var seriesId = Guid.NewGuid();
            var hostSeasonId = Guid.NewGuid();
            var specialsSeasonId = Guid.NewGuid();
            var hostEpisodeId = Guid.NewGuid();
            var specialId = Guid.NewGuid();

            // The Jellyfin item reports the specials season, but the analysis queue tracked the
            // special under the host season, which is where its season-state entry lives.
            var special = CreateEpisode(specialId, seriesId, specialsSeasonId, parentIndexNumber: 0, "/media/show/s00e01.mkv");
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager(special));
            EntrypointTestHelpers.SetPropertyOrField(
                Plugin.Instance!,
                "QueuedMediaItems",
                new ConcurrentDictionary<Guid, List<QueuedEpisode>>
                {
                    [hostSeasonId] =
                    [
                        new QueuedEpisode { EpisodeId = hostEpisodeId, SeasonId = hostSeasonId },
                        new QueuedEpisode { EpisodeId = specialId, SeasonId = hostSeasonId },
                    ],
                });

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(specialId, new TimeRange(100, 160)), AnalysisMode.Introduction);
            await database.SetEpisodeIdsAsync(hostSeasonId, AnalysisMode.Introduction, [hostEpisodeId, specialId], "hash");

            var service = new MediaSegmentEditorService(new FakeJellyfinSegmentStore());
            var controller = new SegmentEditorController(service, database);

            await controller.DeleteSegmentAsync(Guid.NewGuid(), specialId, "intro", CancellationToken.None);

            await using var db = new IntroSkipperDbContext(dbPath);
            var state = await db.DbSeasonState
                .AsNoTracking()
                .SingleAsync(s => s.SeasonId == hostSeasonId && s.Type == AnalysisMode.Introduction);
            Assert.Equal([hostEpisodeId], state.EpisodeIds);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task DeleteSegment_FallsBackToItemSeasonId_WhenQueueIsNotBuilt()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
            var seasonId = Guid.NewGuid();
            var episodeId = Guid.NewGuid();

            // No analysis has run yet: the cached queue is empty, so the editor must fall back
            // to the item's own SeasonId — which is the correct key for regular episodes.
            var episode = CreateEpisode(episodeId, Guid.NewGuid(), seasonId, parentIndexNumber: 1, "/media/show/s01e01.mkv");
            EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager(episode));
            EntrypointTestHelpers.SetPropertyOrField(
                Plugin.Instance!,
                "QueuedMediaItems",
                new ConcurrentDictionary<Guid, List<QueuedEpisode>>());

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            await database.UpdateTimestampAsync(new Segment(episodeId, new TimeRange(100, 160)), AnalysisMode.Introduction);
            await database.SetEpisodeIdsAsync(seasonId, AnalysisMode.Introduction, [episodeId], "hash");

            var service = new MediaSegmentEditorService(new FakeJellyfinSegmentStore());
            var controller = new SegmentEditorController(service, database);

            await controller.DeleteSegmentAsync(Guid.NewGuid(), episodeId, "intro", CancellationToken.None);

            await using var db = new IntroSkipperDbContext(dbPath);
            var state = await db.DbSeasonState
                .AsNoTracking()
                .SingleAsync(s => s.SeasonId == seasonId && s.Type == AnalysisMode.Introduction);
            Assert.Empty(state.EpisodeIds);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    private static Episode CreateEpisode(Guid id, Guid seriesId, Guid seasonId, int parentIndexNumber, string path)
    {
        var episode = new Episode
        {
            Name = "Episode",
            SeriesId = seriesId,
            SeasonId = seasonId,
            ParentIndexNumber = parentIndexNumber,
            IndexNumber = 1,
            Path = path,
        };
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", id);
        EntrypointTestHelpers.SetPropertyOrField(episode, "SeriesName", "Show");
        EntrypointTestHelpers.EnsureNonVirtual(episode);
        return episode;
    }

    private static VirtualFolderInfo NewFolder(string name)
        => new()
        {
            Name = name,
            ItemId = Guid.NewGuid().ToString(),
        };

    private static string CreateTempDbPath()
        => DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-seasonkey.db");

    private static void DeleteSqliteFiles(string dbPath)
        => DatabaseTestHelpers.DeleteSqliteFiles(dbPath);

    // ILibraryManager stub for the queue-building path.
    private class FakeLibraryManager : DispatchProxy
    {
        private List<VirtualFolderInfo> _folders = [];
        private List<BaseItem> _items = [];

        public static ILibraryManager Create(List<VirtualFolderInfo> folders, List<BaseItem> items)
        {
            var proxy = Create<ILibraryManager, FakeLibraryManager>();
            var fake = (FakeLibraryManager)(object)proxy;
            fake._folders = folders;
            fake._items = items;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(ILibraryManager.GetVirtualFolders) => _folders,
                nameof(ILibraryManager.GetItemList) => _items.ToList(),
                nameof(ILibraryManager.GetItemById) => null,
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
        }
    }

}
