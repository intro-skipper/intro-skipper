// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Lifecycle state of a stored segment.
/// </summary>
public enum SegmentState
{
    /// <summary>
    /// The segment is live: returned by reads and synced to Jellyfin.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Tombstone: the user deleted this automatically detected segment. The row is
    /// kept so re-analysis does not re-add an overlapping automatic segment, is
    /// hidden from all normal reads and never synced to Jellyfin.
    /// </summary>
    Suppressed = 1
}
