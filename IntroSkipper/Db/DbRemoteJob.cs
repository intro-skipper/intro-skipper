// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Durable server-owned remote analysis work and its current lease.
/// </summary>
internal sealed class DbRemoteJob
{
    public Guid Id { get; set; }

    public Guid AttemptId { get; set; }

    public string SnapshotJson { get; set; } = string.Empty;

    public string SnapshotHash { get; set; } = string.Empty;

    public string State { get; set; } = "Pending";

    public string? WorkerId { get; set; }

    public string? NodeId { get; set; }

    public long? NodeRevision { get; set; }

    public string? LeaseTokenHash { get; set; }

    public int LeaseGeneration { get; set; }

    public DateTime? LeaseExpiresUtc { get; set; }

    public int Attempts { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
