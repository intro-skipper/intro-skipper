// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Db;

/// <summary>
/// One durable delete of a validated foreign Jellyfin segment row. Unlike the item's
/// own image — which is always re-derivable from the plugin database — a foreign-row
/// delete cannot be recomputed, so it is journaled with its validated target and
/// replayed until applied. Rows are applied in insertion order (the auto-increment
/// <see cref="Id"/>) before the item's image sync.
/// </summary>
public class DbProjectionExternalOperation
{
    /// <summary>
    /// Gets or sets the auto-increment key; doubles as the per-item FIFO order.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the item the deleted row belonged to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin row ID to delete.
    /// </summary>
    public Guid ExternalSegmentId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin type the row carried when the delete was validated.
    /// A row found under the ID with another type is not deleted.
    /// </summary>
    public MediaSegmentType ExpectedType { get; set; }

    /// <summary>
    /// Gets or sets the start ticks the row carried when the delete was validated.
    /// An apply can run long after validation (backoff, mirroring off); a row whose
    /// boundaries changed under the stable ID since then is not deleted.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the end ticks the row carried when the delete was validated;
    /// see <see cref="StartTicks"/>.
    /// </summary>
    public long EndTicks { get; set; }
}
