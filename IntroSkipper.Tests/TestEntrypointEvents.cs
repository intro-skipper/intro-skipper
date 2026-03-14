// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using IntroSkipper.Configuration;
using IntroSkipper.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestEntrypointEvents
{
    [Fact]
    public void OnItemChanged_IgnoresImageUpdates()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", Guid.NewGuid());
        EntrypointTestHelpers.EnsureNonVirtual(movie);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: ItemUpdateType.ImageUpdate);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        var seasonsToAnalyze = EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint);
        Assert.Empty(seasonsToAnalyze);
    }

    [Fact]
    public void OnItemChanged_QueuesMovieId_WhenAutoDetectEnabled()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var movieId = Guid.NewGuid();
        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", movieId);
        EntrypointTestHelpers.EnsureNonVirtual(movie);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        var seasonsToAnalyze = EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint);
        Assert.Contains(movieId, seasonsToAnalyze);
    }

    [Fact]
    public void OnItemChanged_DoesNothing_WhenAutoDetectDisabled()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: false);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", Guid.NewGuid());
        EntrypointTestHelpers.EnsureNonVirtual(movie);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemChanged", args);

        var seasonsToAnalyze = EntrypointTestHelpers.GetSeasonsToAnalyze(entrypoint);
        Assert.Empty(seasonsToAnalyze);
    }

    [Fact]
    public void OnItemRemoved_DeletesCache_ForEpisode()
    {
        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var episodeId = Guid.NewGuid();

        var file1 = Path.Combine(cacheDir, episodeId.ToString("N"));
        var file2 = Path.Combine(cacheDir, episodeId.ToString("N") + "-credits");
        File.WriteAllText(file1, "x");
        File.WriteAllText(file2, "x");

        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var episode = EntrypointTestHelpers.CreateUninitialized<Episode>();
        EntrypointTestHelpers.SetPropertyOrField(episode, "Id", episodeId);
        EntrypointTestHelpers.EnsureNonVirtual(episode);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: episode, updateReason: 0);
        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);
        }

        Assert.False(File.Exists(file1));
        Assert.False(File.Exists(file2));
    }

    [Fact]
    public void OnSettingsChanged_UpdatesConfig_AndSetsAnalyzeAgain()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: false);

        var newConfig = new PluginConfiguration { AutoDetectIntros = true };

        using (new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir()))
        {
            // Ensure AnalyzeAgain starts false.
            var plugin = Plugin.Instance!;
            plugin.AnalyzeAgain = false;

            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnSettingsChanged", (BasePluginConfiguration)newConfig);

            Assert.True(plugin.AnalyzeAgain);
        }

        var storedConfig = (PluginConfiguration)EntrypointTestHelpers.GetPrivateField(entrypoint, "_config");
        Assert.Same(newConfig, storedConfig);
    }

    [Fact]
    public void OnLibraryRefresh_DoesNotSetAnalyzeAgain_WhenAutomaticTaskRunning()
    {
        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);
        EntrypointTestHelpers.SetPrivateField(entrypoint, "_analyzeAgain", false);

        var cts = new System.Threading.CancellationTokenSource();
        EntrypointTestHelpers.SetPrivateStaticField(typeof(Entrypoint), "_cancellationTokenSource", cts);

        try
        {
            var taskResult = EntrypointTestHelpers.CreateTaskResult("RefreshLibrary", TaskCompletionStatus.Completed);
            var args = EntrypointTestHelpers.CreateTaskCompletionEventArgs(taskResult);

            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnLibraryRefresh", args);

            Assert.False((bool)EntrypointTestHelpers.GetPrivateField(entrypoint, "_analyzeAgain"));
        }
        finally
        {
            cts.Dispose();
            EntrypointTestHelpers.SetPrivateStaticField(typeof(Entrypoint), "_cancellationTokenSource", null);
        }
    }
}

public sealed class TestFingerprintCacheDeletionOnRemove
{
    [Fact]
    public void DeletesFingerprintCache_OnMovieRemoval_WhenAutoDetectEnabled()
    {
        var removedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        var removedBaseName = removedId.ToString("N");
        var otherBaseName = otherId.ToString("N");

        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var file1 = Path.Combine(cacheDir, removedBaseName);
        var file2 = Path.Combine(cacheDir, removedBaseName + "-credits");
        var file3 = Path.Combine(cacheDir, removedBaseName + "-blackframes-0-10-v1");
        var otherFile = Path.Combine(cacheDir, otherBaseName);

        File.WriteAllText(file1, "x");
        File.WriteAllText(file2, "x");
        File.WriteAllText(file3, "x");
        File.WriteAllText(otherFile, "x");

        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", removedId);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Path", "C:\\IntroSkipper.Tests\\removed.mkv");
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        Assert.Equal(removedId, movie.Id);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);
        }

        Assert.False(File.Exists(file1));
        Assert.False(File.Exists(file2));
        Assert.False(File.Exists(file3));
        Assert.True(File.Exists(otherFile));
    }

    [Fact]
    public void DoesNotDeleteFingerprintCache_OnMovieRemoval_WhenAutoDetectDisabled()
    {
        var removedId = Guid.NewGuid();
        var removedBaseName = removedId.ToString("N");

        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var file1 = Path.Combine(cacheDir, removedBaseName);
        var file2 = Path.Combine(cacheDir, removedBaseName + "-credits");

        File.WriteAllText(file1, "x");
        File.WriteAllText(file2, "x");

        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: false);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", removedId);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Path", "C:\\IntroSkipper.Tests\\removed.mkv");
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        Assert.Equal(removedId, movie.Id);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);
        }

        Assert.True(File.Exists(file1));
        Assert.True(File.Exists(file2));
    }

    [Fact]
    public void DoesNotDeleteFingerprintCache_WhenIdIsEmpty()
    {
        var removedId = Guid.Empty;
        var removedBaseName = removedId.ToString("N");

        var cacheDir = EntrypointTestHelpers.CreateTempCacheDir();
        var file1 = Path.Combine(cacheDir, removedBaseName);
        File.WriteAllText(file1, "x");

        var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);

        var movie = new Movie();
        EntrypointTestHelpers.SetPropertyOrField(movie, "Id", removedId);
        EntrypointTestHelpers.SetPropertyOrField(movie, "Path", "C:\\IntroSkipper.Tests\\removed.mkv");
        EntrypointTestHelpers.EnsureNonVirtual(movie);
        Assert.Equal(removedId, movie.Id);

        var args = EntrypointTestHelpers.CreateItemChangeEventArgs(item: movie, updateReason: 0);
        using (new EntrypointTestHelpers.PluginInstanceScope(cacheDir))
        {
            EntrypointTestHelpers.InvokePrivate(entrypoint, "OnItemRemoved", args);
        }

        Assert.True(File.Exists(file1));
    }
}

internal static class EntrypointTestHelpers
{
    internal static Entrypoint CreateEntrypoint(bool autoDetectIntros)
    {
        // Entrypoint's ctor reads Plugin.Instance?.Configuration. Ensure Plugin.Instance is null during construction.
        using var _ = new PluginInstanceNullScope();

        var loggerFactory = LoggerFactory.Create(builder => { });
        var logger = loggerFactory.CreateLogger<Entrypoint>();

#pragma warning disable SYSLIB0050 // FormatterServices is obsolete; used only for test scaffolding.
        var mediaSegmentUpdateManager = (IntroSkipper.Manager.MediaSegmentUpdateManager)FormatterServices.GetUninitializedObject(typeof(IntroSkipper.Manager.MediaSegmentUpdateManager));
#pragma warning restore SYSLIB0050

        var entrypoint = new Entrypoint(
            libraryManager: null!,
            providerManager: null!,
            fileSystem: null!,
            taskManager: null!,
            logger: logger,
            loggerFactory: loggerFactory,
            mediaSegmentUpdateManager: mediaSegmentUpdateManager);

        SetPrivateField(entrypoint, "_config", new PluginConfiguration { AutoDetectIntros = autoDetectIntros });
        return entrypoint;
    }

    internal static HashSet<Guid> GetSeasonsToAnalyze(Entrypoint entrypoint)
        => (HashSet<Guid>)GetPrivateField(entrypoint, "_seasonsToAnalyze");

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
        TrySetFirstFieldOfType(item, typeof(LocationType), LocationType.FileSystem);
        TrySetFirstFieldOfType(item, typeof(LocationType?), (LocationType?)LocationType.FileSystem);
    }

    internal static void TrySetFirstFieldOfType(object instance, Type fieldType, object value)
    {
        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == fieldType)
                {
                    field.SetValue(instance, value);
                    return;
                }
            }
        }

        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.Name.Contains("LocationType", StringComparison.OrdinalIgnoreCase) && field.FieldType == fieldType)
                {
                    field.SetValue(instance, value);
                    return;
                }
            }
        }
    }

    internal static T CreateUninitialized<T>() where T : class
    {
#pragma warning disable SYSLIB0050 // FormatterServices is obsolete; used only for test scaffolding.
        return (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
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

        public PluginInstanceScope(string cacheDir)
        {
            CacheDir = cacheDir;

            var instanceProp = typeof(Plugin).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(instanceProp);

            _original = (Plugin?)instanceProp!.GetValue(null);

#pragma warning disable SYSLIB0050 // FormatterServices is obsolete; used only for test scaffolding.
            var plugin = (Plugin)FormatterServices.GetUninitializedObject(typeof(Plugin));
#pragma warning restore SYSLIB0050

            SetPropertyOrField(plugin, "FingerprintCachePath", CacheDir);

            // Plugin.Instance has a private setter; invoke it via reflection.
            var setter = instanceProp.SetMethod ?? instanceProp.GetSetMethod(nonPublic: true);
            Assert.NotNull(setter);
            setter!.Invoke(null, [plugin]);
        }

        public string CacheDir { get; }

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
