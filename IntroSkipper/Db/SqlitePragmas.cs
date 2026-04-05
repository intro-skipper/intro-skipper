// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IntroSkipper.Db;

/// <summary>
/// Shared PRAGMA configuration applied to every SQLite connection on open.
/// Used by <see cref="IntroSkipperDbContext"/> and <see cref="DetectionCacheDbContext"/>.
/// WAL journal mode is not set here because EF Core enables it by default.
/// </summary>
internal static class SqlitePragmas
{
    internal static void Apply(DbCommand cmd)
    {
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }

    internal static async Task ApplyAsync(DbCommand cmd, CancellationToken cancellationToken = default)
    {
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// EF Core connection interceptor that applies SQLite PRAGMAs on every connection open.
/// Shared between <see cref="IntroSkipperDbContext"/> and <see cref="DetectionCacheDbContext"/>.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        try
        {
            SqlitePragmas.Apply(cmd);
        }
        catch (SqliteException)
        {
            // Fall back to SQLite defaults when optional pragmas such as busy_timeout cannot be applied.
        }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        using var cmd = connection.CreateCommand();
        try
        {
            await SqlitePragmas.ApplyAsync(cmd, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Fall back to SQLite defaults when optional pragmas such as busy_timeout cannot be applied.
        }
    }
}
