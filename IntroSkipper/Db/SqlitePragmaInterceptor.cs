// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IntroSkipper.Db;

/// <summary>
/// Applies the per-connection SQLite busy timeout. WAL is enforced during database
/// initialization because journal mode is a persistent database property.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        try
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
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
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Fall back to SQLite defaults when optional pragmas such as busy_timeout cannot be applied.
        }
    }
}
