// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

/// <summary>
/// Eagerly initializes both plugin databases at server startup so migrations and the
/// legacy schema repair run before regular traffic. This is an optimization only:
/// correctness is guaranteed by the initialization gate inside the database facades,
/// which every operation awaits before touching the database.
/// </summary>
public sealed partial class IntroSkipperDatabaseInitializer : IHostedService
{
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
        // only abandons this wait; the shared initialization task keeps running so legacy
        // repair or migration work is never interrupted halfway through.
        try
        {
            await _segmentDatabase.InitializeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
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
        // never blocks on it.
        await Task.Run(_cacheDatabase.TryInitialize, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Eager segment database initialization was deferred; the next database operation will retry")]
    private static partial void LogSegmentWarmupDeferred(ILogger logger, Exception exception);
}
