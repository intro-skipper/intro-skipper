// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// <see cref="IDbContextFactory{TContext}"/> for <see cref="DetectionCacheDbContext"/> that
/// resolves the database file path lazily on every context creation. Used by the
/// transitional <c>Plugin</c> bridge and by tests; production dependency injection uses
/// the options-based factory registered in <c>PluginServiceRegistrator</c>.
/// </summary>
internal sealed class DetectionCacheDbContextPathFactory : IDbContextFactory<DetectionCacheDbContext>
{
    private readonly Func<string> _pathResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDbContextPathFactory"/> class.
    /// </summary>
    /// <param name="pathResolver">Resolves the database file path at context creation time.</param>
    internal DetectionCacheDbContextPathFactory(Func<string> pathResolver)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);
        _pathResolver = pathResolver;
    }

    /// <inheritdoc/>
    public DetectionCacheDbContext CreateDbContext() => new(_pathResolver());
}
