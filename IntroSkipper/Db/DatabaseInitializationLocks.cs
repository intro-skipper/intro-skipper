// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Process-wide serialization of database initialization (legacy schema repair,
/// migrations, cache schema recovery) keyed by database file path.
/// <para>
/// During the transitional period two facade instances can exist per database (the DI
/// singleton and the <c>Plugin</c> bridge), each with its own one-shot gate. Empirically
/// the check-then-ALTER legacy repair does not race between them — Microsoft.Data.Sqlite
/// transactions issue <c>BEGIN IMMEDIATE</c>, so the write lock is taken before the
/// column-existence checks and the loser re-reads the committed schema — and EF Core 9+
/// migrations take their own migration lock. This class serializes the two initializers
/// anyway so the safety argument is local and does not depend on those two external
/// behaviors, and so their log output cannot interleave.
/// </para>
/// </summary>
internal static class DatabaseInitializationLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the process-wide initialization lock for the database targeted by the
    /// given context.
    /// </summary>
    /// <param name="context">Context whose database file identifies the lock.</param>
    /// <returns>The semaphore serializing initialization for that database file.</returns>
    internal static SemaphoreSlim For(DbContext context)
        => _locks.GetOrAdd(GetLockKey(context), _ => new SemaphoreSlim(1, 1));

    private static string GetLockKey(DbContext context)
    {
        var connectionString = context.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return context.GetType().FullName ?? nameof(DbContext);
        }

        try
        {
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            return dataSource is null or "" or ":memory:"
                ? connectionString
                : Path.GetFullPath(dataSource);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or PathTooLongException or NotSupportedException)
        {
            return connectionString;
        }
    }
}
