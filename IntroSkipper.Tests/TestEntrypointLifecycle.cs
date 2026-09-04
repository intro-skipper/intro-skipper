// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
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
        var ffmpegService = new StubFFmpegService { VersionCheck = () => true };
        using var pluginScope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { AutoDetectIntros = true });
        using var entrypoint = EntrypointTestHelpers.CreateEntrypoint(libraryManagerService, ffmpegService);

        await entrypoint.StartAsync(CancellationToken.None);

        Assert.Equal(1, ffmpegService.VersionCheckCalls);
        Assert.Equal(1, libraryManager.ItemAddedSubscriberCount);
        Assert.Equal(1, libraryManager.ItemUpdatedSubscriberCount);
        Assert.Equal(1, libraryManager.ItemRemovedSubscriberCount);

        await entrypoint.StopAsync(CancellationToken.None);

        Assert.Equal(0, libraryManager.ItemAddedSubscriberCount);
        Assert.Equal(0, libraryManager.ItemUpdatedSubscriberCount);
        Assert.Equal(0, libraryManager.ItemRemovedSubscriberCount);
        Assert.Equal(TaskState.Idle, entrypoint.AutomaticTaskState);
    }

    [Fact]
    public async Task CancelAutomaticTaskAsync_CancelsActiveTaskAndRetainsCancellingState()
    {
        using var entrypoint = EntrypointTestHelpers.CreateEntrypoint();
        using var cancellationSource = new CancellationTokenSource();
        var cancellationObserved = false;
        using var registration = cancellationSource.Token.Register(() => cancellationObserved = true);
        EntrypointTestHelpers.SetPrivateField(entrypoint, "_cancellationTokenSource", cancellationSource);

        await entrypoint.CancelAutomaticTaskAsync(CancellationToken.None);

        Assert.True(cancellationObserved);
        Assert.True(cancellationSource.IsCancellationRequested);
        Assert.Equal(TaskState.Cancelling, entrypoint.AutomaticTaskState);
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
}
