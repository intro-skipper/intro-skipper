// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace IntroSkipper.Db;

/// <summary>
/// Shared PRAGMA configuration applied to every SQLite connection on open.
/// Used by <see cref="IntroSkipperDbContext"/> and <see cref="DetectionCacheDbContext"/>.
/// WAL journal mode is not set here because it is a persistent database property:
/// EF Core enables it when it creates a database, and the facade initialization cores
/// re-enforce it idempotently for databases created or rewritten by external tooling.
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
