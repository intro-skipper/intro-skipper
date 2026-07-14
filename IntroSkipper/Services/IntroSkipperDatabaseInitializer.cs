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
        // Blast radius: this runs inside Jellyfin's host startup, so nothing thrown here
        // may propagate — an unhandled exception would abort the entire server, taking
        // every plugin and Jellyfin itself down over a plugin cache file. The facades
        // log and propagate initialization failures, so the warm-up must remain
        // independently exception-proof. A failed warm-up resets the facade gate; the
        // next real operation retries initialization before touching the database.
        //
        // Cancellation only abandons the *wait*: the initialization work itself is
        // intentionally non-cancellable (a half-applied legacy repair would be worse than
        // a slow shutdown) and keeps running inside the facade's shared gate.
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

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
            LogWarmupDeferred(_logger, ex, "segment");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _cacheDatabase.Initialize();
        }
        catch (Exception ex)
        {
            LogWarmupDeferred(_logger, ex, "detection cache");
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Eager {Database} database initialization was deferred; the next database operation will retry")]
    private static partial void LogWarmupDeferred(ILogger logger, Exception exception, string database);
}
