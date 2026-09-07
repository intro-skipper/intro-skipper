// SPDX-FileCopyrightText: 2026 IntroSkipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestPluginDatabaseInitialization
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateContext_RejectsUnfinishedInitialization_AfterStartupTimeout(bool cache)
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var initialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_databaseInitializationTask", initialization.Task);
        Func<DbContext> createContext = cache ? Plugin.CreateCacheDbContext : Plugin.CreateDbContext;

        try
        {
            Assert.False(await Entrypoint.WaitForStartupInitializationAsync(initialization.Task, TimeSpan.Zero));

            var error = await Task.Run(() => Record.Exception(() =>
            {
                using var context = createContext();
            })).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("database access is unavailable", error.Message, StringComparison.Ordinal);
            Assert.False(initialization.Task.IsCompleted);
        }
        finally
        {
            initialization.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateContext_AllowsCompletedInitialization(bool cache)
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_dbPath", Path.Combine(scope.CacheDir, "segments.db"));
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_databaseInitializationTask", Task.CompletedTask);
        Func<DbContext> createContext = cache ? Plugin.CreateCacheDbContext : Plugin.CreateDbContext;

        using var context = createContext();
        Assert.NotNull(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateContext_PropagatesInitializationFailure(bool cache)
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var failure = new InvalidOperationException("Initialization failed");
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_databaseInitializationTask", Task.FromException(failure));
        Func<DbContext> createContext = cache ? Plugin.CreateCacheDbContext : Plugin.CreateDbContext;

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => createContext()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateContext_PropagatesInitializationCancellation(bool cache)
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_databaseInitializationTask", Task.FromCanceled(new CancellationToken(true)));
        Func<DbContext> createContext = cache ? Plugin.CreateCacheDbContext : Plugin.CreateDbContext;

        Assert.ThrowsAny<OperationCanceledException>(() => createContext());
    }

    [Fact]
    public async Task InitializeDatabasesAsync_ObservesCancellationBeforeSchemaWork()
    {
        using var scope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        var initialization = Plugin.Instance!.InitializeDatabasesAsync(new CancellationToken(true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
