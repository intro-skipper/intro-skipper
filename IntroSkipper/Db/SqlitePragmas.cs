// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace IntroSkipper.Db;

/// <summary>
/// Shared PRAGMA configuration applied to every SQLite connection on open.
/// Used by both <see cref="DetectionCacheDb"/> (raw ADO.NET) and the
/// <c>SqlitePragmaInterceptor</c> inside <see cref="IntroSkipperDbContext"/> (EF Core).
/// </summary>
internal static class SqlitePragmas
{
    internal static void Apply(DbCommand cmd)
    {
        // busy_timeout must be set before journal_mode=WAL: changing WAL mode
        // requires an exclusive lock, so the timeout must already be in place.
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    internal static async Task ApplyAsync(DbCommand cmd, CancellationToken cancellationToken = default)
    {
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
