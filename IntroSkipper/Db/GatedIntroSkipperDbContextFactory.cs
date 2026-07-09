// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// <see cref="IDbContextFactory{TContext}"/> decorator that awaits the segment database's
/// one-shot initialization gate (legacy schema repair + EF migrations) before handing out
/// a context. This is the factory registered in dependency injection, so even a future
/// consumer that resolves the raw factory instead of <see cref="IIntroSkipperDatabase"/>
/// cannot query an unmigrated database — the ordering guarantee is structural rather
/// than a per-call-site discipline.
/// <para>
/// The facade itself is constructed over an ungated inner factory
/// (<see cref="DelegateDbContextFactory{TContext}"/>): its initialization core creates
/// contexts, so gating that path would deadlock the gate against itself.
/// </para>
/// </summary>
internal sealed class GatedIntroSkipperDbContextFactory : IDbContextFactory<IntroSkipperDbContext>
{
    private readonly IIntroSkipperDatabase _database;
    private readonly IDbContextFactory<IntroSkipperDbContext> _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedIntroSkipperDbContextFactory"/> class.
    /// </summary>
    /// <param name="database">Segment database facade owning the initialization gate.</param>
    /// <param name="inner">Ungated factory that actually creates contexts.</param>
    internal GatedIntroSkipperDbContextFactory(IIntroSkipperDatabase database, IDbContextFactory<IntroSkipperDbContext> inner)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(inner);
        _database = database;
        _inner = inner;
    }

    /// <inheritdoc/>
    public IntroSkipperDbContext CreateDbContext()
    {
        // Blocking on the gate is safe here: Jellyfin hosts the plugin without a
        // SynchronizationContext (generic host), the gate's continuations run on the
        // thread pool, and the gate never faults (the facade's initialization core is
        // catch-all). At worst a caller blocks once, for the duration of the one-shot
        // initialization. Async call sites should prefer CreateDbContextAsync.
        _database.InitializeAsync().GetAwaiter().GetResult();
        return _inner.CreateDbContext();
    }

    /// <inheritdoc/>
    public async Task<IntroSkipperDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        // WaitAsync abandons the wait on cancellation; the one-shot initialization
        // itself is never cancelled.
        await _database.InitializeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return _inner.CreateDbContext();
    }
}
