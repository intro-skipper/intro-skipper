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
        // already log-and-swallow their own initialization failures, but that is their
        // contract, not a guarantee this warm-up can rely on (a facade bug, an exception
        // from Lazy publication races, or an OutOfMemoryException would still escape), so
        // the warm-up is independently exception-proof. A failed warm-up degrades to the
        // lazy gate: the first real operation retriggers nothing (one-shot) but surfaces
        // its own errors in the normal request/scan paths, matching pre-refactor behavior.
        //
        // Cancellation only abandons the *wait*: the initialization work itself is
        // intentionally non-cancellable (a half-applied legacy repair would be worse than
        // a slow shutdown) and keeps running inside the facade's one-shot gate.
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
            LogWarmupFailed(_logger, ex, "segment");
        }

        try
        {
            _cacheDatabase.Initialize();
        }
        catch (Exception ex)
        {
            LogWarmupFailed(_logger, ex, "detection cache");
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Eager {Database} database initialization failed; the plugin will run against the existing schema and subsequent database operations will surface their own errors")]
    private static partial void LogWarmupFailed(ILogger logger, Exception exception, string database);
}
