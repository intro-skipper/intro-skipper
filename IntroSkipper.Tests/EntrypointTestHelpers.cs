// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using IntroSkipper.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

internal static class EntrypointTestHelpers
{
    internal static readonly byte[] EmptyJsonArray = Encoding.UTF8.GetBytes("[]");

    /// <summary>
    /// Builds an entrypoint over a fresh segment database and the given (or a fresh)
    /// cache database. The entrypoint reads its configuration from
    /// <see cref="Plugin.Instance"/>, so tests scope one via <see cref="CreatePluginScope"/>.
    /// </summary>
    internal static Entrypoint CreateEntrypoint(
        ILibraryManager? libraryManager = null,
        IFFmpegService? ffmpegService = null,
        string? cacheDbPath = null)
    {
        var resolvedCacheDbPath = cacheDbPath ?? DatabaseTestHelpers.CreateTempCacheDbPath();

        // The Entrypoint and its analyzer factory see the same segment database, as
        // they do in production DI.
        var segmentDatabase = DatabaseTestHelpers.CreateTempSegmentDatabase();

        return new Entrypoint(
            libraryManager!,
            DatabaseTestHelpers.CreateCacheDatabase(resolvedCacheDbPath),
            segmentDatabase,
            ffmpegService!,
            NullLogger<Entrypoint>.Instance,
            new AnalyzerTaskFactory(
                NullLoggerFactory.Instance,
                libraryManager!,
                providerManager: null!,
                fileSystem: null!,
                ffmpegService!,
                cacheService: DatabaseTestHelpers.CreateCacheService(resolvedCacheDbPath),
                database: segmentDatabase));
    }

    // Lightweight ILibraryManager stub that resolves the supplied items by id via GetItemById
    // and returns null for any other id. Shared by the controller test suites.
    internal static ILibraryManager CreateLibraryManager(params BaseItem[] items)
        => FakeLibraryManager.Create([], _ => [], id => items.FirstOrDefault(item => item.Id == id));

    // ITaskManager stub with no scheduled task workers, for controllers that look up the
    // detection task's worker state.
    internal static ITaskManager CreateTaskManager()
        => TaskManagerProxy.Create();

    /// <summary>
    /// Scopes a plugin instance carrying the given configuration and an empty analysis queue.
    /// </summary>
    internal static PluginInstanceScope CreatePluginScope(PluginConfiguration configuration, string? cacheDbPath = null)
    {
        var scope = new PluginInstanceScope(CreateTempCacheDir(), cacheDbPath);
        SetPropertyOrField(Plugin.Instance!, "Configuration", configuration);
        SetPropertyOrField(Plugin.Instance!, "QueuedMediaItems", new ConcurrentDictionary<Guid, List<QueuedEpisode>>());
        return scope;
    }

    /// <summary>
    /// Scopes a plugin instance around a single movie library item: the library manager
    /// resolves the movie, the configuration carries the given mirror flag, and the
    /// analysis queue is empty. Shared by the controller test suites.
    /// </summary>
    internal static PluginInstanceScope CreateMoviePluginScope(Guid itemId, bool updateMediaSegments, out Movie item)
    {
        var scope = CreatePluginScope(new PluginConfiguration { UpdateMediaSegments = updateMediaSegments });
        item = JellyfinItems.Movie(itemId);
        SetPrivateField(Plugin.Instance!, "_libraryManager", CreateLibraryManager(item));
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

    /// <summary>
    /// ILibraryManager stub for the queue-building path. Returns the configured virtual
    /// folders and answers GetItemList either from a per-library delegate (which may throw
    /// to simulate a failed library enumeration; ids then resolve through the given
    /// resolver, null by default: "the server does not know this item") or from a fixed
    /// item list filtered the way the queue manager queries it (by season ancestors, by
    /// in-season specials, by explicit ids). The item-list form resolves ids without a
    /// backing item to a Season stub, so scoped runs can ask for a season's owning
    /// libraries through GetCollectionFolders.
    /// </summary>
    internal class FakeLibraryManager : DispatchProxy
    {
        private List<VirtualFolderInfo> _folders = [];
        private Func<InternalItemsQuery?, List<BaseItem>> _getItemList = _ => [];
        private Func<Guid, BaseItem?> _getItemById = _ => null;
        private Dictionary<Guid, Guid> _owningFolderIds = [];

        /// <summary>Gets the ParentId of every GetItemList query, so tests can assert which libraries were queried.</summary>
        public List<Guid> QueriedLibraryIds { get; } = [];

        public static ILibraryManager Create(
            List<VirtualFolderInfo> folders,
            Func<Guid, List<BaseItem>> getItemList,
            Func<Guid, BaseItem?>? getItemById = null)
            => Create(folders, query => getItemList(query?.ParentId ?? Guid.Empty), getItemById ?? (_ => null), []);

        public static ILibraryManager Create(
            List<VirtualFolderInfo> folders,
            IReadOnlyList<BaseItem> items,
            Dictionary<Guid, Guid>? owningFolderIds = null)
            => Create(
                folders,
                query => Filter(items, query),
                id => items.FirstOrDefault(item => item.Id == id) ?? new Season { Id = id },
                owningFolderIds ?? []);

        private static ILibraryManager Create(
            List<VirtualFolderInfo> folders,
            Func<InternalItemsQuery?, List<BaseItem>> getItemList,
            Func<Guid, BaseItem?> getItemById,
            Dictionary<Guid, Guid> owningFolderIds)
        {
            var proxy = Create<ILibraryManager, FakeLibraryManager>();
            var fake = (FakeLibraryManager)(object)proxy;
            fake._folders = folders;
            fake._getItemList = getItemList;
            fake._getItemById = getItemById;
            fake._owningFolderIds = owningFolderIds;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                nameof(ILibraryManager.GetVirtualFolders) => _folders,
                nameof(ILibraryManager.GetItemList) => GetItems(args?.OfType<InternalItemsQuery>().FirstOrDefault()),
                nameof(ILibraryManager.GetItemById) => _getItemById(args?.OfType<Guid>().FirstOrDefault() ?? Guid.Empty),
                nameof(ILibraryManager.GetCollectionFolders) => CollectionFoldersFor((BaseItem)args![0]!),
                _ => throw new NotImplementedException(targetMethod?.Name),
            };
        }

        private static List<BaseItem> Filter(IReadOnlyList<BaseItem> items, InternalItemsQuery? query)
        {
            if (query?.ParentIndexNumber == 0 && query.AncestorIds is { Length: > 0 })
            {
                return [.. items.Where(item => item is Episode episode &&
                    episode.ParentIndexNumber == query.ParentIndexNumber &&
                    query.AncestorIds.Contains(episode.SeriesId))];
            }

            if (query?.AncestorIds is { Length: > 0 })
            {
                return [.. items.Where(item => item is Episode episode && query.AncestorIds.Contains(episode.SeasonId))];
            }

            if (query?.ItemIds is { Length: > 0 })
            {
                return [.. items.Where(item => query.ItemIds.Contains(item.Id))];
            }

            return [.. items];
        }

        private List<BaseItem> GetItems(InternalItemsQuery? query)
        {
            if (query is not null)
            {
                QueriedLibraryIds.Add(query.ParentId);
            }

            return _getItemList(query);
        }

        // Owning libraries per requested id; ids without a mapping are owned by every folder,
        // which preserves the query-every-library behavior for tests that don't care.
        private List<Folder> CollectionFoldersFor(BaseItem item)
        {
            IEnumerable<Guid> folderIds = _owningFolderIds.TryGetValue(item.Id, out var owner)
                ? [owner]
                : _folders.Select(folder => Guid.Parse(folder.ItemId!));
            return [.. folderIds.Select(id => new Folder { Id = id })];
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

            // The cache DB lives outside cacheDir so legacy file sweeps cannot touch it.
            // Nothing creates it here: the cache facade builds the schema on first use,
            // so tests that never reach the cache pay for no database file.
            CacheDbPath = cacheDbPath ?? DatabaseTestHelpers.CreateTempCacheDbPath();

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
}
