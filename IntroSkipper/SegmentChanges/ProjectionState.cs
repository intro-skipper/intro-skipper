// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Durable projection lifecycle of one item's recorded work.</summary>
public enum ProjectionState
{
    /// <summary>Projection work is recorded and waiting to be applied.</summary>
    Pending,

    /// <summary>No work is pending; Jellyfin reflects the last accepted change.</summary>
    Applied,

    /// <summary>Mirroring is disabled; the recorded work stays durable and replays on enable.</summary>
    Skipped
}
