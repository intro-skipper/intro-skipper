// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using IntroSkipper.Db;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Tests for the gated <see cref="IDbContextFactory{TContext}"/> decorators: the
/// structural guarantee that no context handed out by the registered factories can
/// observe an unmigrated (or uncreated) database, even when the consumer bypasses the
/// facades — plus the DI wiring that keeps the facades themselves on ungated inner
/// factories so their initialization cannot deadlock against its own gate.
/// </summary>
public sealed class TestGatedContextFactories
{
    [Fact]
    public async Task GatedSegmentFactory_ConcurrentFirstUseOnVirginDatabase_YieldsMigratedContexts()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            var options = new DbContextOptionsBuilder<IntroSkipperDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            var inner = new DelegateDbContextFactory<IntroSkipperDbContext>(() => new IntroSkipperDbContext(options));
            var facade = new IntroSkipperDatabase(inner, NullLogger.Instance);
            var gated = new GatedIntroSkipperDbContextFactory(facade, inner);

            // Without the gate, querying a virgin database file would throw
            // "no such table". Mix the sync and async factory paths to also pin
            // that the sync path cannot deadlock on the gate.
#pragma warning disable CA1849 // Exercising the synchronous factory path is the point of this test.
            var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                using var context = i % 2 == 0
                    ? gated.CreateDbContext()
                    : await gated.CreateDbContextAsync();
                return await context.DbSegment.CountAsync();
            })));
#pragma warning restore CA1849

            Assert.All(counts, count => Assert.Equal(0, count));

            await using var db = new IntroSkipperDbContext(dbPath);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task GatedCacheFactory_FirstUseOnVirginDatabase_YieldsCreatedSchema()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            var options = new DbContextOptionsBuilder<DetectionCacheDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            var inner = new DelegateDbContextFactory<DetectionCacheDbContext>(() => new DetectionCacheDbContext(options));
            var facade = new DetectionCacheDatabase(inner, NullLogger.Instance);
            var gated = new GatedDetectionCacheDbContextFactory(facade, inner);

#pragma warning disable CA1849 // Exercising the synchronous factory path is the point of this test.
            var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
            {
                using var context = i % 2 == 0
                    ? gated.CreateDbContext()
                    : await gated.CreateDbContextAsync();
                return await context.DetectionCache.CountAsync();
            })));
#pragma warning restore CA1849

            Assert.All(counts, count => Assert.Equal(0, count));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ServiceRegistrations_GateRegisteredFactories_AndKeepFacadesUngated()
    {
        var dataPath = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "gated-di", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ApplicationPathsProxy.Create(dataPath));
            new PluginServiceRegistrator().RegisterServices(services, ServerApplicationHostProxy.Create());

            await using var provider = services.BuildServiceProvider();

            // The registered factories are the gated decorators.
            var segmentFactory = provider.GetRequiredService<IDbContextFactory<IntroSkipperDbContext>>();
            Assert.IsType<GatedIntroSkipperDbContextFactory>(segmentFactory);
            var cacheFactory = provider.GetRequiredService<IDbContextFactory<DetectionCacheDbContext>>();
            Assert.IsType<GatedDetectionCacheDbContextFactory>(cacheFactory);

            // The facade initializes and operates without deadlocking on its own gate
            // (it must be wired to the ungated inner factory)...
            var database = provider.GetRequiredService<IIntroSkipperDatabase>();
            Assert.Empty(await database.GetSegmentsAsync(Guid.NewGuid()));

            // ...and a raw-factory consumer receives a migrated/created context.
            using (var context = segmentFactory.CreateDbContext())
            {
                Assert.Equal(0, await context.DbSegment.CountAsync());
            }

            using (var cacheContext = cacheFactory.CreateDbContext())
            {
                Assert.Equal(0, await cacheContext.DetectionCache.CountAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static string CreateTempDbPath()
    {
        var tempDir = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "gated-factories");
        Directory.CreateDirectory(tempDir);
        return Path.Join(tempDir, Guid.NewGuid().ToString("N") + ".db");
    }

    private static void DeleteSqliteFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }.Where(File.Exists))
        {
            File.Delete(path);
        }
    }

    // Minimal IApplicationPaths stub: the DB registrations only read DataPath.
    private class ApplicationPathsProxy : DispatchProxy
    {
        private string _dataPath = string.Empty;

        public static IApplicationPaths Create(string dataPath)
        {
            var proxy = Create<IApplicationPaths, ApplicationPathsProxy>();
            ((ApplicationPathsProxy)(object)proxy)._dataPath = dataPath;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == $"get_{nameof(IApplicationPaths.DataPath)}")
            {
                return _dataPath;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    // RegisterServices never dereferences the host; any proxy member access throws.
    private class ServerApplicationHostProxy : DispatchProxy
    {
        public static IServerApplicationHost Create()
            => Create<IServerApplicationHost, ServerApplicationHostProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotImplementedException(targetMethod?.Name);
    }
}
