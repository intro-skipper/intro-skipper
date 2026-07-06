// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using Microsoft.Extensions.Hosting;

namespace IntroSkipper.Services;

/// <summary>
/// Eagerly initializes both plugin databases at server startup so migrations and the
/// legacy schema repair run before regular traffic. This is an optimization only:
/// correctness is guaranteed by the initialization gate inside the database facades,
/// which every operation awaits before touching the database.
/// </summary>
public sealed class IntroSkipperDatabaseInitializer : IHostedService
{
    private readonly IIntroSkipperDatabase _segmentDatabase;
    private readonly IDetectionCacheDatabase _cacheDatabase;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSkipperDatabaseInitializer"/> class.
    /// </summary>
    /// <param name="segmentDatabase">Segment database facade.</param>
    /// <param name="cacheDatabase">Detection cache database facade.</param>
    public IntroSkipperDatabaseInitializer(IIntroSkipperDatabase segmentDatabase, IDetectionCacheDatabase cacheDatabase)
    {
        _segmentDatabase = segmentDatabase;
        _cacheDatabase = cacheDatabase;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Initialization failures are logged inside the facades and intentionally do not
        // fail server startup, matching the plugin's historical constructor behavior.
        await _segmentDatabase.InitializeAsync().ConfigureAwait(false);
        _cacheDatabase.Initialize();
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
