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
