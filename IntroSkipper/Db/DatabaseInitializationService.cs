// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Hosting;

namespace IntroSkipper.Db;

/// <summary>
/// Hosted service that eagerly kicks off one-time database initialization at server startup
/// so the first playback query does not pay the migration cost. Correctness does not depend
/// on this service: the gated context factories await the same init task lazily.
/// </summary>
internal sealed class DatabaseInitializationService : IHostedService
{
    private readonly DatabaseInitializer _initializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInitializationService"/> class.
    /// </summary>
    /// <param name="initializer">Database initializer.</param>
    public DatabaseInitializationService(DatabaseInitializer initializer)
    {
        _initializer = initializer;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
        => _initializer.EnsureInitializedAsync(cancellationToken);

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
