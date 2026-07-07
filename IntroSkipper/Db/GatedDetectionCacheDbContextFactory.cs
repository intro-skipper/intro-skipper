// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// <see cref="IDbContextFactory{TContext}"/> for <see cref="DetectionCacheDbContext"/> that
/// blocks context creation until one-time database initialization (create-or-recover) has completed.
/// </summary>
internal sealed class GatedDetectionCacheDbContextFactory : IDbContextFactory<DetectionCacheDbContext>
{
    private readonly DatabaseInitializer _initializer;
    private readonly DbContextOptions<DetectionCacheDbContext> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedDetectionCacheDbContextFactory"/> class.
    /// </summary>
    /// <param name="initializer">Database initializer providing the init gate.</param>
    /// <param name="options">Context options.</param>
    public GatedDetectionCacheDbContextFactory(DatabaseInitializer initializer, DbContextOptions<DetectionCacheDbContext> options)
    {
        _initializer = initializer;
        _options = options;
    }

    /// <inheritdoc/>
    public DetectionCacheDbContext CreateDbContext()
    {
        _initializer.EnsureCacheDatabaseInitialized();
        return new DetectionCacheDbContext(_options);
    }

    /// <inheritdoc/>
    public async Task<DetectionCacheDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        await _initializer.EnsureCacheDatabaseInitializedAsync(cancellationToken).ConfigureAwait(false);
        return new DetectionCacheDbContext(_options);
    }
}
