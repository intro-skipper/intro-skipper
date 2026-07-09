// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// <see cref="IDbContextFactory{TContext}"/> decorator that runs the detection cache
/// database's one-shot schema gate (create-or-recover) before handing out a context.
/// This is the factory registered in dependency injection, so even a future consumer
/// that resolves the raw factory instead of <see cref="IDetectionCacheDatabase"/>
/// cannot query the cache before its schema exists.
/// <para>
/// The facade itself is constructed over an ungated inner factory
/// (<see cref="DelegateDbContextFactory{TContext}"/>): its initialization core creates
/// contexts, so gating that path would deadlock the gate against itself. The cache gate
/// is synchronous (<c>Lazy&lt;bool&gt;</c>), so neither factory path involves
/// sync-over-async blocking.
/// </para>
/// </summary>
internal sealed class GatedDetectionCacheDbContextFactory : IDbContextFactory<DetectionCacheDbContext>
{
    private readonly IDetectionCacheDatabase _database;
    private readonly IDbContextFactory<DetectionCacheDbContext> _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedDetectionCacheDbContextFactory"/> class.
    /// </summary>
    /// <param name="database">Detection cache facade owning the initialization gate.</param>
    /// <param name="inner">Ungated factory that actually creates contexts.</param>
    internal GatedDetectionCacheDbContextFactory(IDetectionCacheDatabase database, IDbContextFactory<DetectionCacheDbContext> inner)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(inner);
        _database = database;
        _inner = inner;
    }

    /// <inheritdoc/>
    public DetectionCacheDbContext CreateDbContext()
    {
        _database.Initialize();
        return _inner.CreateDbContext();
    }

    /// <inheritdoc/>
    public Task<DetectionCacheDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDbContext());
    }
}
