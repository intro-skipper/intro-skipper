// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.FFmpeg;
using IntroSkipper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestEntrypointLifecycle
{
    [Fact]
    public async Task StartAsync_SubscribesHandlersUntilStopAsync()
    {
        var libraryManager = LibraryManagerEventProxy.Create(out var libraryManagerService);
        var taskManager = TaskManagerEventProxy.Create(out var taskManagerService);
        var ffmpegService = FFmpegVersionProxy.Create(out var ffmpegServiceProxy);
        using var entrypoint = EntrypointTestHelpers.CreateEntrypoint(autoDetectIntros: true);
        EntrypointTestHelpers.SetPrivateField(entrypoint, "_libraryManager", libraryManagerService);
        EntrypointTestHelpers.SetPrivateField(entrypoint, "_taskManager", taskManagerService);
        EntrypointTestHelpers.SetPrivateField(entrypoint, "_ffmpegService", ffmpegService);

        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());

        await entrypoint.StartAsync(CancellationToken.None);

        Assert.Equal(1, ffmpegServiceProxy.CheckVersionCallCount);
        Assert.Equal(1, libraryManager.ItemAddedSubscriberCount);
        Assert.Equal(1, libraryManager.ItemUpdatedSubscriberCount);
        Assert.Equal(1, libraryManager.ItemRemovedSubscriberCount);
        Assert.Equal(1, taskManager.TaskCompletedSubscriberCount);

        await entrypoint.StopAsync(CancellationToken.None);

        Assert.Equal(0, libraryManager.ItemAddedSubscriberCount);
        Assert.Equal(0, libraryManager.ItemUpdatedSubscriberCount);
        Assert.Equal(0, libraryManager.ItemRemovedSubscriberCount);
        Assert.Equal(0, taskManager.TaskCompletedSubscriberCount);
        Assert.Equal(TaskState.Idle, Entrypoint.AutomaticTaskState);
    }

    [Fact]
    public async Task CancelAutomaticTaskAsync_CancelsActiveTaskAndRetainsCancellingState()
    {
        using var cancellationSource = new CancellationTokenSource();
        var cancellationObserved = false;
        using var registration = cancellationSource.Token.Register(() => cancellationObserved = true);
        EntrypointTestHelpers.SetPrivateStaticField(typeof(Entrypoint), "_cancellationTokenSource", cancellationSource);

        try
        {
            await Entrypoint.CancelAutomaticTaskAsync(CancellationToken.None);

            Assert.True(cancellationObserved);
            Assert.True(cancellationSource.IsCancellationRequested);
            Assert.Equal(TaskState.Cancelling, Entrypoint.AutomaticTaskState);
        }
        finally
        {
            EntrypointTestHelpers.SetPrivateStaticField(typeof(Entrypoint), "_cancellationTokenSource", null);
        }
    }

    private class LibraryManagerEventProxy : DispatchProxy
    {
        internal int ItemAddedSubscriberCount { get; private set; }

        internal int ItemUpdatedSubscriberCount { get; private set; }

        internal int ItemRemovedSubscriberCount { get; private set; }

        internal static LibraryManagerEventProxy Create(out ILibraryManager service)
        {
            service = Create<ILibraryManager, LibraryManagerEventProxy>();
            return (LibraryManagerEventProxy)(object)service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "add_ItemAdded":
                    ItemAddedSubscriberCount++;
                    return null;
                case "remove_ItemAdded":
                    ItemAddedSubscriberCount--;
                    return null;
                case "add_ItemUpdated":
                    ItemUpdatedSubscriberCount++;
                    return null;
                case "remove_ItemUpdated":
                    ItemUpdatedSubscriberCount--;
                    return null;
                case "add_ItemRemoved":
                    ItemRemovedSubscriberCount++;
                    return null;
                case "remove_ItemRemoved":
                    ItemRemovedSubscriberCount--;
                    return null;
                default:
                    throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }

    private class TaskManagerEventProxy : DispatchProxy
    {
        internal int TaskCompletedSubscriberCount { get; private set; }

        internal static TaskManagerEventProxy Create(out ITaskManager service)
        {
            service = Create<ITaskManager, TaskManagerEventProxy>();
            return (TaskManagerEventProxy)(object)service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "add_TaskCompleted":
                    TaskCompletedSubscriberCount++;
                    return null;
                case "remove_TaskCompleted":
                    TaskCompletedSubscriberCount--;
                    return null;
                default:
                    throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }

    private class FFmpegVersionProxy : DispatchProxy
    {
        internal int CheckVersionCallCount { get; private set; }

        internal static IFFmpegService Create(out FFmpegVersionProxy proxy)
        {
            var service = Create<IFFmpegService, FFmpegVersionProxy>();
            proxy = (FFmpegVersionProxy)(object)service;
            return service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IFFmpegService.CheckFFmpegVersionAsync))
            {
                CheckVersionCallCount++;
                return Task.FromResult(true);
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }
}
