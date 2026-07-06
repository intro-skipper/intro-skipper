// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Creates <see cref="DetectionCacheDbContext"/> instances bound to a fixed SQLite database path.
/// The <see cref="DbContextOptions{TContext}"/> are built once so every context shares the same
/// options instance (and therefore the same cached EF internal service provider).
/// </summary>
internal sealed class DetectionCacheDbContextFactory : IDbContextFactory<DetectionCacheDbContext>
{
    private readonly DbContextOptions<DetectionCacheDbContext> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDbContextFactory"/> class.
    /// </summary>
    /// <param name="dbPath">Path of the SQLite database file.</param>
    public DetectionCacheDbContextFactory(string dbPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);

        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _options = new DbContextOptionsBuilder<DetectionCacheDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .AddInterceptors(SqlitePragmaInterceptor.Instance)
            .Options;
    }

    /// <inheritdoc/>
    public DetectionCacheDbContext CreateDbContext() => new(_options);
}
