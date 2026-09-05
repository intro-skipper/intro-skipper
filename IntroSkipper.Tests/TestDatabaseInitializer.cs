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
/// Pins the hosted warm-up's startup isolation and cancellation behavior.
/// </summary>
public sealed class TestDatabaseInitializer
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_WarmsBothDatabasesOnce_AndSegmentFailureNeverEscapes(bool segmentWarmupFails)
    {
        var segmentCalls = 0;
        var cacheCalls = 0;
        var segmentDatabase = FacadeProxy.CreateSegmentDatabase(() =>
        {
            segmentCalls++;
            return segmentWarmupFails
                ? Task.FromException(new IOException("simulated async segment init failure"))
                : Task.CompletedTask;
        });
        var cacheDatabase = FacadeProxy.CreateCacheDatabase(() => cacheCalls++);
        var initializer = new IntroSkipperDatabaseInitializer(
            segmentDatabase, cacheDatabase, NullLogger<IntroSkipperDatabaseInitializer>.Instance);

        Assert.Null(await Record.ExceptionAsync(() => initializer.StartAsync(CancellationToken.None)));
        Assert.Equal(1, segmentCalls);
        Assert.Equal(1, cacheCalls);
        Assert.Null(await Record.ExceptionAsync(() => initializer.StopAsync(CancellationToken.None)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_CancellationDuringOrAfterSegmentWarmup_ReturnsWithoutThrowingAndSkipsCache(bool warmupCompletes)
    {
        var cacheCalls = 0;
        using var cts = new CancellationTokenSource();
        // Cancel from inside the fake so the token is untouched at method entry (exercising
        // the WaitAsync path, not the early-return check); the incomplete variant's task
        // never completes.
        var segmentDatabase = FacadeProxy.CreateSegmentDatabase(async () =>
        {
            await cts.CancelAsync();
            if (!warmupCompletes)
            {
                await new TaskCompletionSource().Task;
            }
        });
        var cacheDatabase = FacadeProxy.CreateCacheDatabase(() => cacheCalls++);
        var initializer = new IntroSkipperDatabaseInitializer(
            segmentDatabase, cacheDatabase, NullLogger<IntroSkipperDatabaseInitializer>.Instance);

        // Bound the wait: without cancellation support StartAsync would hang forever on the
        // never-completing warm-up; the timeout turns that regression into a test failure.
        Assert.Null(await Record.ExceptionAsync(
            () => initializer.StartAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30))));
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(0, cacheCalls);
    }

    [Fact]
    public async Task StartAsync_CancellationDuringCacheWarmup_ReturnsWithoutInterruptingCache()
    {
        using var cts = new CancellationTokenSource();
        using var releaseCache = new ManualResetEventSlim();
        var cacheStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cacheCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var segmentDatabase = FacadeProxy.CreateSegmentDatabase(() => Task.CompletedTask);
        var cacheDatabase = FacadeProxy.CreateCacheDatabase(() =>
        {
            cacheStarted.SetResult();
            releaseCache.Wait();
            cacheCompleted.SetResult();
        });
        var initializer = new IntroSkipperDatabaseInitializer(
            segmentDatabase, cacheDatabase, NullLogger<IntroSkipperDatabaseInitializer>.Instance);

        var startTask = initializer.StartAsync(cts.Token);
        await cacheStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            await cts.CancelAsync();
            Assert.Null(await Record.ExceptionAsync(
                () => startTask.WaitAsync(TimeSpan.FromSeconds(30))));
            Assert.False(cacheCompleted.Task.IsCompleted);
        }
        finally
        {
            releaseCache.Set();
        }

        await cacheCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
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

        public static IDetectionCacheDatabase CreateCacheDatabase(Action tryInitialize)
        {
            var proxy = Create<IDetectionCacheDatabase, FacadeProxy>();
            var typed = (FacadeProxy)(object)proxy;
            typed._memberName = nameof(IDetectionCacheDatabase.TryInitialize);
            typed._handler = () =>
            {
                tryInitialize();
                return true;
            };
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == _memberName
                ? _handler()
                : throw new NotImplementedException(targetMethod?.Name);
    }
}
