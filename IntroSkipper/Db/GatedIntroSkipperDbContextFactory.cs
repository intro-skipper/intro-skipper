// SPDX-FileCopyrightText: 2026 intro-skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// <see cref="IDbContextFactory{TContext}"/> for <see cref="IntroSkipperDbContext"/> that
/// blocks context creation until one-time database initialization (legacy repair plus
/// migrations) has completed. Because every runtime consumer obtains contexts through this
/// factory, no query can run against an unmigrated database.
/// </summary>
internal sealed class GatedIntroSkipperDbContextFactory : IDbContextFactory<IntroSkipperDbContext>
{
    private readonly DatabaseInitializer _initializer;
    private readonly DbContextOptions<IntroSkipperDbContext> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedIntroSkipperDbContextFactory"/> class.
    /// </summary>
    /// <param name="initializer">Database initializer providing the init gate.</param>
    /// <param name="options">Context options.</param>
    public GatedIntroSkipperDbContextFactory(DatabaseInitializer initializer, DbContextOptions<IntroSkipperDbContext> options)
    {
        _initializer = initializer;
        _options = options;
    }

    /// <inheritdoc/>
    public IntroSkipperDbContext CreateDbContext()
    {
        _initializer.EnsureSegmentDatabaseInitialized();
        return new IntroSkipperDbContext(_options);
    }

    /// <inheritdoc/>
    public async Task<IntroSkipperDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        await _initializer.EnsureSegmentDatabaseInitializedAsync(cancellationToken).ConfigureAwait(false);
        return new IntroSkipperDbContext(_options);
    }
}
