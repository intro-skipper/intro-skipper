// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Capy Agent
// SPDX-FileCopyrightText: 2026 Claude Fable 5
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Eagerly initializes both plugin databases at server startup so migrations and the
/// one-time legacy import run before regular traffic. This is an optimization only:
/// correctness is guaranteed by the initialization gate inside the database facades,
/// which every operation awaits before touching the database.
/// </summary>
internal sealed partial class IntroSkipperDatabaseInitializer : IHostedService
{
    private static readonly TimeSpan _databaseInitializationTimeout = TimeSpan.FromSeconds(30);
    private readonly IIntroSkipperDatabase _segmentDatabase;
    private readonly IDetectionCacheDatabase _cacheDatabase;
    private readonly ILogger<IntroSkipperDatabaseInitializer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDatabaseInitializer"/> class.
    /// </summary>
    /// <param name="segmentDatabase">Segment database facade.</param>
    /// <param name="cacheDatabase">Detection cache database facade.</param>
    /// <param name="logger">Logger.</param>
    public IntroSkipperDatabaseInitializer(
        IIntroSkipperDatabase segmentDatabase,
        IDetectionCacheDatabase cacheDatabase,
        ILogger<IntroSkipperDatabaseInitializer> logger)
    {
        _segmentDatabase = segmentDatabase;
        _cacheDatabase = cacheDatabase;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Segment initialization can fail and must not abort Jellyfin startup. Cancellation
        // or the timeout only abandons this wait; the shared initialization task keeps
        // running so the legacy import or a migration is never interrupted halfway through.
        try
        {
            if (!await WaitForStartupInitializationAsync(
                    _segmentDatabase.InitializeAsync(),
                    _databaseInitializationTimeout,
                    cancellationToken).ConfigureAwait(false))
            {
                LogDatabaseInitializationTimedOut("Segment", _databaseInitializationTimeout.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host startup is being aborted; stop waiting and skip the remaining warm-up
            // (including the cache init) — the host is shutting down anyway.
            return;
        }
        catch (Exception ex)
        {
            LogSegmentWarmupDeferred(_logger, ex);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // The cache init is synchronous SQLite I/O (schema creation, possibly a
        // corrupt-file rebuild); run it on the thread pool so the startup thread
        // never blocks on it. Cancellation only abandons the wait so recovery is
        // never interrupted halfway through.
        try
        {
            var cacheInitialization = Task.Run(_cacheDatabase.TryInitialize, CancellationToken.None);
            if (!await WaitForStartupInitializationAsync(
                    cacheInitialization,
                    _databaseInitializationTimeout,
                    cancellationToken).ConfigureAwait(false))
            {
                LogDatabaseInitializationTimedOut("Detection cache", _databaseInitializationTimeout.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static async Task<bool> WaitForStartupInitializationAsync(
        Task initializationTask,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var completedTask = await Task.WhenAny(
                initializationTask,
                Task.Delay(timeout, cancellationToken))
            .ConfigureAwait(false);

        if (completedTask == initializationTask)
        {
            await initializationTask.ConfigureAwait(false);
            return true;
        }

        _ = initializationTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Database} database initialization exceeded its startup timeout of {TimeoutSeconds} seconds; initialization will continue in the background")]
    private partial void LogDatabaseInitializationTimedOut(string database, double timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Eager segment database initialization was deferred; the next database operation will retry")]
    private static partial void LogSegmentWarmupDeferred(ILogger logger, Exception exception);
}
