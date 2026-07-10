// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Db;
using IntroSkipper.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Pins the hosted warm-up's blast-radius contract: <see cref="IntroSkipperDatabaseInitializer.StartAsync"/>
/// runs inside Jellyfin's host startup, where an escaping exception would abort the entire
/// server, so facade initialization failures of any shape must be swallowed (and merely
/// logged) here, independent of the facades' own catch policies.
/// </summary>
public sealed class TestDatabaseInitializer
{
    [Fact]
    public async Task StartAsync_FacadeInitializationFailures_NeverEscape()
    {
        var segmentDatabase = FacadeProxy.CreateSegmentDatabase(
            () => Task.FromException(new IOException("simulated async segment init failure")));
        var cacheDatabase = FacadeProxy.CreateCacheDatabase(
            () => throw new InvalidOperationException("simulated sync cache init failure"));
        var initializer = new IntroSkipperDatabaseInitializer(
            segmentDatabase, cacheDatabase, NullLogger<IntroSkipperDatabaseInitializer>.Instance);

        Assert.Null(await Record.ExceptionAsync(() => initializer.StartAsync(CancellationToken.None)));
        Assert.Null(await Record.ExceptionAsync(() => initializer.StopAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task StartAsync_WarmsBothDatabases_ExactlyOnce()
    {
        var segmentCalls = 0;
        var cacheCalls = 0;
        var segmentDatabase = FacadeProxy.CreateSegmentDatabase(() =>
        {
            segmentCalls++;
            return Task.CompletedTask;
        });
        var cacheDatabase = FacadeProxy.CreateCacheDatabase(() => cacheCalls++);
        var initializer = new IntroSkipperDatabaseInitializer(
            segmentDatabase, cacheDatabase, NullLogger<IntroSkipperDatabaseInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, segmentCalls);
        Assert.Equal(1, cacheCalls);
    }

    [Fact]
    public async Task StartAsync_CancellationDuringSegmentWarmup_ReturnsWithoutThrowingAndSkipsCache()
    {
        var cacheCalls = 0;
        using var cts = new CancellationTokenSource();
        // Cancel from inside the fake so the token is untouched at method entry (exercising
        // the WaitAsync path, not the early-return check) and the returned task never completes.
        var segmentDatabase = FacadeProxy.CreateSegmentDatabase(async () =>
        {
            await cts.CancelAsync();
            await new TaskCompletionSource().Task;
        });
        var cacheDatabase = FacadeProxy.CreateCacheDatabase(() => cacheCalls++);
        var initializer = new IntroSkipperDatabaseInitializer(
            segmentDatabase, cacheDatabase, NullLogger<IntroSkipperDatabaseInitializer>.Instance);

        // Bound the wait: without cancellation support StartAsync would hang forever on the
        // never-completing warm-up; the timeout turns that regression into a test failure.
        Assert.Null(await Record.ExceptionAsync(
            () => initializer.StartAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30))));
        Assert.Equal(0, cacheCalls);
    }

    // Strict facade fakes: only the initialization member is stubbed; any other member
    // access throws, proving the warm-up touches nothing else.
    private class FacadeProxy : DispatchProxy
    {
        private string _memberName = string.Empty;
        private Func<object?> _handler = () => null;

        public static IIntroSkipperDatabase CreateSegmentDatabase(Func<Task> initializeAsync)
        {
            var proxy = Create<IIntroSkipperDatabase, FacadeProxy>();
            var typed = (FacadeProxy)(object)proxy;
            typed._memberName = nameof(IIntroSkipperDatabase.InitializeAsync);
            typed._handler = () => initializeAsync();
            return proxy;
        }

        public static IDetectionCacheDatabase CreateCacheDatabase(Action initialize)
        {
            var proxy = Create<IDetectionCacheDatabase, FacadeProxy>();
            var typed = (FacadeProxy)(object)proxy;
            typed._memberName = nameof(IDetectionCacheDatabase.Initialize);
            typed._handler = () =>
            {
                initialize();
                return null;
            };
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == _memberName
                ? _handler()
                : throw new NotImplementedException(targetMethod?.Name);
    }
}
