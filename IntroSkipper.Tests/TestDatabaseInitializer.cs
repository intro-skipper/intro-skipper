// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Db;
using IntroSkipper.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Pins the hosted warm-up's startup isolation and cancellation behavior.
/// </summary>
public sealed class TestDatabaseInitializer
{
    [Fact]
    public async Task WaitForStartupInitializationAsync_ReturnsFalse_WhenInitializationTimesOut()
    {
        var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var completed = await IntroSkipperDatabaseInitializer.WaitForStartupInitializationAsync(
            initialization.Task,
            TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(completed);
        Assert.False(initialization.Task.IsCompleted);
        Assert.True(initialization.TrySetResult());
        await initialization.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task StartAsync_BothDatabasesTimeOut_ContinuesWarmupAndLogsEachDatabase()
    {
        var segmentInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCache = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cacheCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var segmentCalls = 0;
        var cacheCalls = 0;
        var segmentDatabase = FacadeProxy.CreateSegmentDatabase(() =>
        {
            segmentCalls++;
            return segmentInitialization.Task;
        });
        var cacheDatabase = FacadeProxy.CreateCacheDatabase(() =>
        {
            Interlocked.Increment(ref cacheCalls);
            releaseCache.Task.GetAwaiter().GetResult();
            cacheCompleted.SetResult();
        });
        var logger = new TimeoutLogger();
        var initializer = new IntroSkipperDatabaseInitializer(segmentDatabase, cacheDatabase, logger);

        try
        {
            await initializer.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(90));

            Assert.Equal(1, segmentCalls);
            Assert.Equal(1, Volatile.Read(ref cacheCalls));
            Assert.False(segmentInitialization.Task.IsCompleted);
            Assert.False(cacheCompleted.Task.IsCompleted);
            Assert.Collection(
                logger.Warnings,
                warning => Assert.Equal("Segment database initialization exceeded its startup timeout of 30 seconds; initialization will continue in the background", warning),
                warning => Assert.Equal("Detection cache database initialization exceeded its startup timeout of 30 seconds; initialization will continue in the background", warning));
        }
        finally
        {
            segmentInitialization.TrySetResult();
            releaseCache.TrySetResult();
        }

        await segmentInitialization.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cacheCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

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

    private sealed class TimeoutLogger : ILogger<IntroSkipperDatabaseInitializer>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
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
