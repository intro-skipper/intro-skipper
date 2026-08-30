// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using IntroSkipper.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

internal static class EntrypointTestHelpers
{
    internal static readonly byte[] EmptyJsonArray = Encoding.UTF8.GetBytes("[]");

    internal static Entrypoint CreateEntrypoint(bool autoDetectIntros, string? cacheDbPath = null)
    {
        // Entrypoint's ctor reads Plugin.Instance?.Configuration. Ensure Plugin.Instance is null during construction.
        using var _ = new PluginInstanceNullScope();

        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<Entrypoint>();
        var resolvedCacheDbPath = cacheDbPath ?? DatabaseTestHelpers.CreateTempCacheDbPath();

        // The Entrypoint and its analyzer factory see the same segment database and
        // refresher, as they do in production DI.
        var segmentDatabase = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var mediaSegmentRefresher = new FakeMediaSegmentRefresher();

        var entrypoint = new Entrypoint(
            libraryManager: null!,
            taskManager: null!,
            cacheDatabase: DatabaseTestHelpers.CreateCacheDatabase(resolvedCacheDbPath),
            database: segmentDatabase,
            ffmpegService: null!,
            logger: logger,
            analyzerFactory: new AnalyzerTaskFactory(
                loggerFactory,
                libraryManager: null!,
                providerManager: null!,
                fileSystem: null!,
                mediaSegmentRefresher: mediaSegmentRefresher,
                ffmpegService: null!,
                cacheService: DatabaseTestHelpers.CreateCacheService(resolvedCacheDbPath),
                database: segmentDatabase),
            mediaSegmentRefresher: mediaSegmentRefresher);

        SetPrivateField(entrypoint, "_config", new PluginConfiguration { AutoDetectIntros = autoDetectIntros });
        return entrypoint;
    }

    private sealed class FakeMediaSegmentRefresher : IMediaSegmentRefresher
    {
        public Task RefreshAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveIntroSkipperSegmentsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // Lightweight ILibraryManager stub that resolves the supplied items by id via GetItemById
    // and returns null for any other id. Shared by the controller and refresh-service tests.
    internal static ILibraryManager CreateLibraryManager(params BaseItem[] items)
        => LibraryManagerProxy.Create(items);

    // ITaskManager stub with no scheduled task workers, for controllers that look up the
    // detection task's worker state.
    internal static ITaskManager CreateTaskManager()
        => TaskManagerProxy.Create();

    /// <summary>
    /// Scopes a plugin instance around a single movie library item: the library manager
    /// resolves the movie, the configuration carries the given mirror flag, and the
    /// analysis queue is empty. Shared by the controller test suites.
    /// </summary>
    internal static PluginInstanceScope CreateMoviePluginScope(Guid itemId, bool updateMediaSegments, out Movie item)
    {
        var scope = new PluginInstanceScope(CreateTempCacheDir());
        item = new Movie();
        SetPropertyOrField(item, "Id", itemId);
        EnsureNonVirtual(item);

        var plugin = Plugin.Instance!;
        SetPropertyOrField(plugin, "Configuration", new PluginConfiguration { UpdateMediaSegments = updateMediaSegments });
        SetPrivateField(plugin, "_libraryManager", CreateLibraryManager(item));
        SetPropertyOrField(plugin, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());
        return scope;
    }

    private class TaskManagerProxy : DispatchProxy
    {
        public static ITaskManager Create()
            => Create<ITaskManager, TaskManagerProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == $"get_{nameof(ITaskManager.ScheduledTasks)}")
            {
                return Array.Empty<IScheduledTaskWorker>();
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    private class LibraryManagerProxy : DispatchProxy
    {
        private Dictionary<Guid, BaseItem> _items = [];

        public static ILibraryManager Create(BaseItem[] items)
        {
            var proxy = Create<ILibraryManager, LibraryManagerProxy>();
            var map = new Dictionary<Guid, BaseItem>();
            foreach (var item in items)
            {
                map[item.Id] = item;
            }

            ((LibraryManagerProxy)(object)proxy)._items = map;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemById) && args is [Guid id])
            {
                return _items.TryGetValue(id, out var item) ? item : null;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    internal static HashSet<Guid> GetSeasonsToAnalyze(Entrypoint entrypoint)
        => (HashSet<Guid>)GetPrivateField(entrypoint, "_seasonsToAnalyze");

    internal static Dictionary<Guid, Guid> GetItemsToReset(Entrypoint entrypoint)
        => (Dictionary<Guid, Guid>)GetPrivateField(entrypoint, "_itemsToReset");

    internal static ItemChangeEventArgs CreateItemChangeEventArgs(object item, ItemUpdateType updateReason)
    {
#pragma warning disable SYSLIB0050 // FormatterServices is obsolete; used only for test scaffolding.
        var args = (ItemChangeEventArgs)FormatterServices.GetUninitializedObject(typeof(ItemChangeEventArgs));
#pragma warning restore SYSLIB0050

        SetPropertyOrField(args, "Item", item);
        SetPropertyOrField(args, "UpdateReason", updateReason);
        return args;
    }

    internal static TaskResult CreateTaskResult(string key, TaskCompletionStatus status)
    {
#pragma warning disable SYSLIB0050 // FormatterServices is obsolete; used only for test scaffolding.
        var result = (TaskResult)FormatterServices.GetUninitializedObject(typeof(TaskResult));
#pragma warning restore SYSLIB0050
        SetPropertyOrField(result, "Key", key);
        SetPropertyOrField(result, "Status", status);
        return result;
    }

    internal static TaskCompletionEventArgs CreateTaskCompletionEventArgs(TaskResult result)
    {
#pragma warning disable SYSLIB0050 // FormatterServices is obsolete; used only for test scaffolding.
        var args = (TaskCompletionEventArgs)FormatterServices.GetUninitializedObject(typeof(TaskCompletionEventArgs));
#pragma warning restore SYSLIB0050
        SetPropertyOrField(args, "Result", result);
        return args;
    }

    internal static void InvokePrivate(Entrypoint entrypoint, string methodName, object arg)
    {
        var method = typeof(Entrypoint).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(entrypoint, [null, arg]);
    }

    internal static object GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance)!;
    }

    internal static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    internal static void SetPrivateStaticField(Type type, string fieldName, object? value)
    {
        var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(null, value);
    }

    internal static void SetPropertyOrField(object instance, string name, object value)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (prop is not null)
            {
                var setter = prop.SetMethod ?? prop.GetSetMethod(nonPublic: true);
                if (setter is not null)
                {
                    setter.Invoke(instance, [value]);
                    return;
                }
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                field.SetValue(instance, value);
                return;
            }

            var backing = type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (backing is not null)
            {
                backing.SetValue(instance, value);
                return;
            }
        }

        throw new InvalidOperationException($"Could not set property or field '{name}' on type '{instance.GetType().FullName}'.");
    }

    internal static void EnsureNonVirtual(object item)
    {
        for (var type = item.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(LocationType))
                {
                    field.SetValue(item, LocationType.FileSystem);
                }
                else if (field.FieldType == typeof(LocationType?))
                {
                    field.SetValue(item, (LocationType?)LocationType.FileSystem);
                }
            }
        }
    }

    internal static string CreateTempCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "IntroSkipper.Tests", "chromaprints", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal sealed class PluginInstanceScope : IDisposable
    {
        private readonly Plugin? _original;

        public PluginInstanceScope(string cacheDir, string? cacheDbPath = null)
        {
            CacheDir = cacheDir;
            // Place the cache DB outside cacheDir to avoid accidental inclusion in legacy file sweeps.
            var cacheBaseDir = Path.Combine(
                Path.GetTempPath(),
                Path.GetFileName("IntroSkipper.Tests"));
            CacheDbPath = cacheDbPath ?? Path.Combine(
                cacheBaseDir, Guid.NewGuid().ToString("N") + "-cache.db");

            var instanceProp = typeof(Plugin).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(instanceProp);

            _original = (Plugin?)instanceProp!.GetValue(null);

#pragma warning disable SYSLIB0050 // FormatterServices is obsolete; used only for test scaffolding.
            var plugin = (Plugin)FormatterServices.GetUninitializedObject(typeof(Plugin));
#pragma warning restore SYSLIB0050

            SetPropertyOrField(plugin, "FingerprintCachePath", CacheDir);

            // A default configuration so code reading Plugin.Instance.Configuration (e.g.
            // the media-segment mirror gate) sees defaults instead of an uninitialized
            // lazy loader; tests overwrite it for specific flag values.
            SetPropertyOrField(plugin, "Configuration", new Configuration.PluginConfiguration());

            // Plugin.Instance has a private setter; invoke it via reflection.
            var setter = instanceProp.SetMethod ?? instanceProp.GetSetMethod(nonPublic: true);
            Assert.NotNull(setter);
            setter!.Invoke(null, [plugin]);

            // Ensure the schema exists so tests can write to the cache DB. Create the
            // containing directory first so this scope works regardless of test order.
            Directory.CreateDirectory(Path.GetDirectoryName(CacheDbPath)!);
            using var cacheDb = new IntroSkipper.Db.DetectionCacheDbContext(CacheDbPath);
            cacheDb.EnsureSchema();
        }

        public string CacheDir { get; }

        public string CacheDbPath { get; }

        public void Dispose()
        {
            var instanceProp = typeof(Plugin).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var setter = instanceProp!.SetMethod ?? instanceProp.GetSetMethod(nonPublic: true);
            setter!.Invoke(null, [_original]);
        }
    }

    private sealed class PluginInstanceNullScope : IDisposable
    {
        private readonly Plugin? _original;

        public PluginInstanceNullScope()
        {
            var instanceProp = typeof(Plugin).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(instanceProp);

            _original = (Plugin?)instanceProp!.GetValue(null);

            var setter = instanceProp.SetMethod ?? instanceProp.GetSetMethod(nonPublic: true);
            Assert.NotNull(setter);
            setter!.Invoke(null, [null]);
        }

        public void Dispose()
        {
            var instanceProp = typeof(Plugin).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var setter = instanceProp!.SetMethod ?? instanceProp.GetSetMethod(nonPublic: true);
            setter!.Invoke(null, [_original]);
        }
    }
}
