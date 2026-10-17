// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Durable server-owned declaration and availability state for one scanner node.
/// </summary>
internal sealed class DbRemoteNode
{
    public string NodeId { get; set; } = string.Empty;

    public string ScopeJson { get; set; } = string.Empty;

    public string? PolicyJson { get; set; }

    public string? RuntimeJson { get; set; }

    public bool Enabled { get; set; }

    public long Revision { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}
