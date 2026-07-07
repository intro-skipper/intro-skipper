// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using Microsoft.Extensions.Hosting;

namespace IntroSkipper.Services;

/// <summary>
/// Hosted service that eagerly triggers database initialization at server startup, before Jellyfin
/// serves any request. This is an optimization, not the correctness guarantee: every store
/// operation independently awaits the <see cref="IDatabaseInitializer"/> gate, so even a request
/// that races server startup cannot query an unmigrated database.
/// Both gates swallow initialization failures (log-and-continue), so <see cref="StartAsync"/>
/// cannot fault host startup; the only exception it may surface is cooperative cancellation of
/// the startup token itself.
/// Registered before <see cref="Entrypoint"/> so it starts first.
/// </summary>
internal sealed class DatabaseStartupService : IHostedService
{
    private readonly IDatabaseInitializer _databaseInitializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseStartupService"/> class.
    /// </summary>
    /// <param name="databaseInitializer">Database initializer.</param>
    public DatabaseStartupService(IDatabaseInitializer databaseInitializer)
    {
        _databaseInitializer = databaseInitializer;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _databaseInitializer.EnsureSegmentDbReadyAsync(cancellationToken).ConfigureAwait(false);
        _databaseInitializer.EnsureCacheDbReady();
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
