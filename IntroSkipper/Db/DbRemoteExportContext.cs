// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Explicit offline manifest authorization bound to its immutable snapshot.
/// </summary>
internal sealed class DbRemoteExportContext
{
    public Guid Id { get; set; }

    public string SnapshotJson { get; set; } = string.Empty;

    public string SnapshotHash { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime? ExpiresUtc { get; set; }
}
