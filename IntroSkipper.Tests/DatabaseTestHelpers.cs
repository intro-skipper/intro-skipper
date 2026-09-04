// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using IntroSkipper.Controllers;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using IntroSkipper.SegmentChanges;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Helpers for constructing the database facades in tests over explicit temp-file
/// SQLite paths. Tests that scope <see cref="Plugin.Instance"/> via
/// <see cref="EntrypointTestHelpers.PluginInstanceScope"/> pass the scope's cache
/// database path (<c>scope.CacheDbPath</c>) explicitly.
/// </summary>
internal static class DatabaseTestHelpers
{
    /// <summary>
    /// Builds context options over a SQLite file the way production does (provider plus
    /// the per-connection pragma interceptor), so test contexts share its pragma policy.
    /// </summary>
    internal static DbContextOptions<TContext> CreateContextOptions<TContext>(string dbPath)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        SqlitePragmas.Configure(builder, dbPath);
        return builder.Options;
    }

    internal static IntroSkipperDbContext CreateSegmentContext(string dbPath)
        => new(CreateContextOptions<IntroSkipperDbContext>(dbPath));

    internal static DetectionCacheDbContext CreateCacheContext(string dbPath)
        => new(CreateContextOptions<DetectionCacheDbContext>(dbPath));

    internal static IntroSkipperDatabase CreateSegmentDatabase(string dbPath)
        => new(new TestDbContextFactory<IntroSkipperDbContext>(() => CreateSegmentContext(dbPath)), NullLogger<IntroSkipperDatabase>.Instance);

    /// <summary>
    /// Creates a segment database facade over a fresh temp-file path, for consumers
    /// (e.g. analyzers) whose tests never reach the database. The file is only created
    /// if an operation is actually performed.
    /// </summary>
    /// <returns>The segment database facade.</returns>
    internal static IntroSkipperDatabase CreateTempSegmentDatabase()
        => CreateSegmentDatabase(CreateTempDbPath(Guid.NewGuid().ToString("N") + ".db"));

    /// <summary>
    /// Composes the standard mirror over a store and database, the single test home of
    /// the mirror wiring so constructor changes touch one place. Without an explicit
    /// policy the mirror follows the plugin configuration, as in production.
    /// </summary>
    internal static MediaSegmentMirror CreateMirror(IJellyfinSegmentStore store, IIntroSkipperDatabase database, IMediaSegmentMirrorPolicy? policy = null)
        => new(store, new SegmentDtoFactory(database), policy ?? new MediaSegmentMirrorPolicy());

    /// <summary>
    /// Composes the durable segment-change coordinator over the real Jellyfin
    /// projection adapter and mirror, the single test home of the production
    /// composition chain so constructor changes touch one place. Controller tests get
    /// end-to-end behavior: intent commit, journal, and mirror convergence against
    /// the given store.
    /// </summary>
    internal static SegmentChange CreateSegmentChange(IJellyfinSegmentStore store, IntroSkipperDatabase database, IMediaSegmentMirrorPolicy? policy = null)
    {
        policy ??= new MediaSegmentMirrorPolicy();
        return new(
            database,
            database,
            new JellyfinSegmentProjectionAdapter(store, CreateMirror(store, database, policy), NullLogger<JellyfinSegmentProjectionAdapter>.Instance),
            policy,
            new SegmentMutationLocks(),
            TimeProvider.System,
            NullLogger<SegmentChange>.Instance);
    }

    /// <summary>
    /// Composes the editor controller over the standard segment-change wiring, the
    /// single test home of the controller composition chain.
    /// </summary>
    internal static SegmentEditorController CreateSegmentEditorController(IJellyfinSegmentStore store, IntroSkipperDatabase database)
        => new(CreateSegmentChange(store, database));

    /// <summary>
    /// Converts seconds to ticks for test fixtures; shared so per-file shims are unneeded.
    /// </summary>
    internal static long Ticks(double seconds) => TickConversions.FromSeconds(seconds);

    internal static DetectionCacheDatabase CreateCacheDatabase(string dbPath)
        => new(new TestDbContextFactory<DetectionCacheDbContext>(() => CreateCacheContext(dbPath)), NullLogger<DetectionCacheDatabase>.Instance);

    internal static DetectionCacheService CreateCacheService(string dbPath)
        => new(NullLogger<DetectionCacheService>.Instance, CreateCacheDatabase(dbPath));

    /// <summary>
    /// Creates a fresh temp-file cache database path. The file is only created when a
    /// context or facade actually operates on it.
    /// </summary>
    /// <returns>The cache database path.</returns>
    internal static string CreateTempCacheDbPath()
        => CreateTempDbPath(Guid.NewGuid().ToString("N") + "-cache.db");

    /// <summary>
    /// Creates a detection cache service over a fresh temp-file path, for consumers
    /// whose tests never reach the cache database (e.g. fingerprint caching disabled).
    /// The file is only created if a cache operation is actually performed.
    /// </summary>
    /// <returns>The detection cache service.</returns>
    internal static DetectionCacheService CreateTempCacheService()
        => CreateCacheService(CreateTempCacheDbPath());

    /// <summary>
    /// Returns a database path under the shared test temp directory, creating the
    /// directory so SQLite can open the file regardless of which test runs first.
    /// </summary>
    /// <param name="fileName">Database file name.</param>
    /// <returns>The database path.</returns>
    internal static string CreateTempDbPath(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    /// <summary>
    /// Deletes a SQLite database and its WAL/SHM sidecar files. Clears pooled
    /// connections first so Windows file locks from earlier contexts cannot make the
    /// delete flaky.
    /// </summary>
    /// <param name="dbPath">Database path.</param>
    internal static void DeleteSqliteFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
