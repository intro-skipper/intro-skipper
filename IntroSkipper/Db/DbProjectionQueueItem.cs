// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Durable marker that an item's Jellyfin projection is behind the plugin database.
/// The row records work, not data: applying it re-projects the item's current truth,
/// so a marker that survives a crash costs one extra idempotent sync and can never
/// replay a stale image. A row exists exactly while work is pending; applying deletes
/// it, guarded by <see cref="Version"/> so a concurrent re-enqueue is never lost.
/// </summary>
public class DbProjectionQueueItem
{
    /// <summary>
    /// Gets or sets the item whose projection is pending. One row per item.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the enqueue version. Every accepted change bumps it; the applied
    /// delete is conditioned on the version it projected, so work accepted while a
    /// projection was in flight keeps the row alive.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// Gets or sets the number of failed projection attempts of the pending work.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Gets or sets the backoff due time. <see langword="null"/> means due immediately —
    /// enqueue never consults a clock, so test time providers stay authoritative.
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// Gets or sets the sanitized message of the latest projection failure.
    /// </summary>
    public string? Failure { get; set; }
}
