// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for <see cref="CleanCacheTask"/>. The cleanup task deletes every row that is NOT in
/// the enumerated library queue, so these tests pin the guards that keep an incomplete or
/// empty enumeration from mass-deleting healthy data.
/// </summary>
public sealed class TestCleanCacheTask
{
    [Fact]
    public async Task ExecuteAsync_SkipsAllCleanup_WhenALibraryFailsToEnumerate()
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var liveEpisodeId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { UpdateMediaSegments = false });

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
            await SeedAsync(database, cacheDatabase, liveEpisodeId);

            // One library exists but its item enumeration throws, so the queue is incomplete.
            var libraryManager = FakeLibraryManager.Create(
                [NewFolder("Shows")],
                _ => throw new InvalidOperationException("library database unavailable"));

            var progress = new RecordingProgress();
            await CreateTask(libraryManager, database, cacheDatabase).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);
            await AssertSeededDataIntactAsync(database, cacheDatabase, liveEpisodeId, dbPath);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SkipsAllCleanup_WhenNoEnabledLibrariesHaveItems()
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var liveEpisodeId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { UpdateMediaSegments = false });

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);
            await SeedAsync(database, cacheDatabase, liveEpisodeId);

            // No virtual folders at all: an empty queue must not classify everything as stale.
            var libraryManager = FakeLibraryManager.Create([], _ => []);

            var progress = new RecordingProgress();
            await CreateTask(libraryManager, database, cacheDatabase).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);
            await AssertSeededDataIntactAsync(database, cacheDatabase, liveEpisodeId, dbPath);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeletesOnlyStaleRows_WhenEnumerationSucceeds()
    {
        var (dbPath, cacheDbPath) = CreateTempDbPaths();
        var movieId = Guid.NewGuid();
        var staleEpisodeId = Guid.NewGuid();
        try
        {
            using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir(), cacheDbPath);
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { UpdateMediaSegments = false });

            var database = DatabaseTestHelpers.CreateSegmentDatabase(dbPath);
            var cacheDatabase = DatabaseTestHelpers.CreateCacheDatabase(cacheDbPath);

            // Live movie data plus rows for an item that is no longer in any library.
            await database.UpdateTimestampAsync(new Segment(movieId, new TimeRange(10, 40)), AnalysisMode.Introduction);
            await database.SetEpisodeIdsAsync(movieId, AnalysisMode.Introduction, [movieId], "hash");
            await SeedAsync(database, cacheDatabase, staleEpisodeId);

            var movie = new MediaBrowser.Controller.Entities.Movies.Movie
            {
                Id = movieId,
                Name = "Test Movie",
                Path = "/media/test-movie.mkv",
            };
            var libraryManager = FakeLibraryManager.Create([NewFolder("Movies")], _ => [movie]);

            var progress = new RecordingProgress();
            await CreateTask(libraryManager, database, cacheDatabase).ExecuteAsync(progress, CancellationToken.None);

            Assert.Equal(100, progress.Value);

            Assert.NotEmpty(await database.GetSegmentsAsync(movieId));
            Assert.Empty(await database.GetSegmentsAsync(staleEpisodeId));
            Assert.Empty(await cacheDatabase.GetStaleItemIdsAsync(new HashSet<Guid> { movieId }));

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.DbSeasonState, s => s.SeasonId == movieId));
            Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.DbSeasonState, s => s.SeasonId == staleEpisodeId));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
            DeleteSqliteFiles(cacheDbPath);
        }
    }

    private static CleanCacheTask CreateTask(ILibraryManager libraryManager, IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase)
        => new(
            NullLogger<CleanCacheTask>.Instance,
            NullLoggerFactory.Instance,
            libraryManager,
            providerManager: null!,
            fileSystem: null!,
            ffmpegService: null!,
            database,
            cacheDatabase,
            mediaSegmentRefresher: null!);

    private static async Task SeedAsync(IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase, Guid episodeId)
    {
        await database.UpdateTimestampAsync(new Segment(episodeId, new TimeRange(5, 65)), AnalysisMode.Introduction);
        await database.SetEpisodeIdsAsync(episodeId, AnalysisMode.Introduction, [episodeId], "hash");
        cacheDatabase.Upsert(episodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0, EntrypointTestHelpers.EmptyJsonArray, "hash");
    }

    private static async Task AssertSeededDataIntactAsync(IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase, Guid episodeId, string dbPath)
    {
        Assert.NotEmpty(await database.GetSegmentsAsync(episodeId));
        Assert.NotNull(cacheDatabase.FindEntry(episodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 0));

        await using var db = new IntroSkipperDbContext(dbPath);
        Assert.True(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            db.DbSeasonState, s => s.SeasonId == episodeId));
    }

    private static VirtualFolderInfo NewFolder(string name)
        => new()
        {
            Name = name,
            ItemId = Guid.NewGuid().ToString(),
        };

    private static (string DbPath, string CacheDbPath) CreateTempDbPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(directory);
        return (
            Path.Combine(directory, Guid.NewGuid().ToString("N") + "-cleantask.db"),
            Path.Combine(directory, Guid.NewGuid().ToString("N") + "-cleantask-cache.db"));
    }

    private static void DeleteSqliteFiles(string dbPath)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public double? Value { get; private set; }

        public void Report(double value) => Value = value;
    }

    // ILibraryManager stub for the queue-building path: returns the configured virtual
    // folders and delegates GetItemList to the configured behavior (which may throw to
    // simulate a failed library enumeration).
    private class FakeLibraryManager : DispatchProxy
    {
        private List<VirtualFolderInfo> _folders = [];
        private Func<Guid, List<BaseItem>> _getItemList = _ => [];

        public static ILibraryManager Create(List<VirtualFolderInfo> folders, Func<Guid, List<BaseItem>> getItemList)
        {
            var proxy = Create<ILibraryManager, FakeLibraryManager>();
            var fake = (FakeLibraryManager)(object)proxy;
            fake._folders = folders;
            fake._getItemList = getItemList;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(ILibraryManager.GetVirtualFolders) => _folders,
                nameof(ILibraryManager.GetItemList) => _getItemList(ExtractParentId(args)).ToList(),
                nameof(ILibraryManager.GetItemById) => null,
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
        }

        private static Guid ExtractParentId(object?[]? args)
            => args?.OfType<InternalItemsQuery>().FirstOrDefault()?.ParentId ?? Guid.Empty;
    }
}
