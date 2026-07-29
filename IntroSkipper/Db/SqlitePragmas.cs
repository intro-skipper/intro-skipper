// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace IntroSkipper.Db;

/// <summary>
/// SQLite pragma policy shared by both plugin databases.
/// </summary>
internal static class SqlitePragmas
{
    /// <summary>
    /// Per-connection busy timeout applied by <see cref="SqlitePragmaInterceptor"/>.
    /// </summary>
    public const string BusyTimeoutCommand = "PRAGMA busy_timeout=5000;";

    private const string WalCommand = "PRAGMA journal_mode=WAL;";

    private static readonly SqlitePragmaInterceptor _pragmaInterceptor = new();

    /// <summary>
    /// Applies the shared SQLite configuration for a file-backed plugin database: the
    /// provider plus the per-connection pragma interceptor. Every context configuration
    /// site goes through this so the pragma policy cannot drift.
    /// </summary>
    /// <param name="options">The options builder to configure.</param>
    /// <param name="dbPath">Path to the SQLite database file.</param>
    public static void Configure(DbContextOptionsBuilder options, string dbPath)
        => options.UseSqlite($"Data Source={dbPath}").AddInterceptors(_pragmaInterceptor);

    /// <summary>
    /// Enforces WAL journal mode. WAL is a persistent database property, but EF only
    /// sets it when *it* creates the database file. Enforce it idempotently so databases
    /// vacuumed or recreated by external tooling are covered as well.
    /// </summary>
    /// <param name="database">The database facade to apply the pragma to.</param>
    public static void EnforceWal(DatabaseFacade database)
        => database.ExecuteSqlRaw(WalCommand);

    /// <summary>
    /// Enforces WAL journal mode asynchronously. See <see cref="EnforceWal"/>.
    /// </summary>
    /// <param name="database">The database facade to apply the pragma to.</param>
    /// <returns>A task that completes when the pragma has been applied.</returns>
    public static Task EnforceWalAsync(DatabaseFacade database)
        => database.ExecuteSqlRawAsync(WalCommand);
}
