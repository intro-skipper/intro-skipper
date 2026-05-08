// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;

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
