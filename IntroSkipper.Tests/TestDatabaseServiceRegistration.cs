// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// DI-wiring regression test: runs the real <see cref="PluginServiceRegistrator"/>,
/// resolves both database facades from the built provider and performs one real
/// round-trip through each. This historically caught a compiled-query cross-model bug,
/// so the resolution + operation coverage must survive any registration refactor.
/// </summary>
public sealed class TestDatabaseServiceRegistration
{
    [Fact]
    public async Task ServiceRegistrations_ResolveFacades_ThatOperateOverTheDiFactories()
    {
        var dataPath = Path.Join(Path.GetTempPath(), "IntroSkipper.Tests", "facade-di", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ApplicationPathsProxy.Create(dataPath));
            new PluginServiceRegistrator().RegisterServices(services, ServerApplicationHostProxy.Create());

            await using var provider = services.BuildServiceProvider();

            var database = provider.GetRequiredService<IIntroSkipperDatabase>();
            var episodeId = Guid.NewGuid();
            await database.ReplaceAutoSegmentsAsync(
                episodeId, AnalysisMode.Introduction, [new Segment(episodeId, new TimeRange(10, 60))], SegmentSource.Chapter);
            var stored = Assert.Single(await database.GetSegmentsAsync(episodeId));
            Assert.Equal(episodeId, stored.ItemId);

            var cacheDatabase = provider.GetRequiredService<IDetectionCacheDatabase>();
            var itemId = Guid.NewGuid();
            cacheDatabase.Upsert(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10, EntrypointTestHelpers.EmptyJsonArray, string.Empty);
            Assert.NotNull(cacheDatabase.FindEntry(itemId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 10));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataPath, recursive: true);
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
