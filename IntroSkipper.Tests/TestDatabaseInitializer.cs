// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Pins the hosted warm-up's blast-radius contract: <see cref="IntroSkipperDatabaseInitializer.StartAsync"/>
/// runs inside Jellyfin's host startup, where an escaping exception would abort the entire
/// server, so propagated facade initialization failures of any shape must be contained
/// here, independent of the facades' retry policies.
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
    public async Task StartAsync_FailedWarmup_LeavesBothFacadesRetryable()
    {
        var directory = Path.Join(
            Path.GetTempPath(),
            "IntroSkipper.Tests",
            "database-initializer",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var segmentPath = Path.Join(directory, "segments.db");
        var cachePath = Path.Join(directory, "cache.db");
        var segmentContextCreations = 0;
        var cacheContextCreations = 0;

        var segmentDatabase = new IntroSkipperDatabase(
            new TestDbContextFactory<IntroSkipperDbContext>(() =>
            {
                if (Interlocked.Increment(ref segmentContextCreations) == 1)
                {
                    throw new IOException("Simulated segment warm-up failure.");
                }

                return new IntroSkipperDbContext(segmentPath);
            }),
            NullLogger.Instance);
        var cacheDatabase = new DetectionCacheDatabase(
            new TestDbContextFactory<DetectionCacheDbContext>(() =>
            {
                if (Interlocked.Increment(ref cacheContextCreations) == 1)
                {
                    throw new IOException("Simulated cache warm-up failure.");
                }

                return new DetectionCacheDbContext(cachePath);
            }),
            NullLogger.Instance);
        var initializer = new IntroSkipperDatabaseInitializer(
            segmentDatabase, cacheDatabase, NullLogger<IntroSkipperDatabaseInitializer>.Instance);

        try
        {
            Assert.Null(await Record.ExceptionAsync(
                () => initializer.StartAsync(CancellationToken.None)));
            Assert.Equal(1, Volatile.Read(ref segmentContextCreations));
            Assert.Equal(1, Volatile.Read(ref cacheContextCreations));

            Assert.Empty(await segmentDatabase.GetSegmentsAsync(Guid.NewGuid()));
            Assert.Null(cacheDatabase.FindEntry(
                Guid.NewGuid(), AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 30));

            Assert.Equal(3, Volatile.Read(ref segmentContextCreations));
            Assert.Equal(3, Volatile.Read(ref cacheContextCreations));

            await segmentDatabase.InitializeAsync();
            cacheDatabase.Initialize();
            Assert.Equal(3, Volatile.Read(ref segmentContextCreations));
            Assert.Equal(3, Volatile.Read(ref cacheContextCreations));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
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

    [Fact]
    public async Task StartAsync_CancellationAfterCompletedSegmentWarmup_SkipsCache()
    {
        var cacheCalls = 0;
        using var cts = new CancellationTokenSource();
        var segmentDatabase = FacadeProxy.CreateSegmentDatabase(() =>
        {
            _ = cts.CancelAsync();
            return Task.CompletedTask;
        });
        var cacheDatabase = FacadeProxy.CreateCacheDatabase(() => cacheCalls++);
        var initializer = new IntroSkipperDatabaseInitializer(
            segmentDatabase, cacheDatabase, NullLogger<IntroSkipperDatabaseInitializer>.Instance);

        await initializer.StartAsync(cts.Token);

        Assert.True(cts.IsCancellationRequested);
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
