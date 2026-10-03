// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// The kind of stored row that refuses an automatic segment
/// (<see cref="AutoSegmentAdmissionPolicy.Check"/>).
/// </summary>
internal enum AutoSegmentRejection
{
    /// <summary>Nothing refuses the segment; it is admitted.</summary>
    None,

    /// <summary>A tombstone of the same mode: the user deleted an overlapping range.</summary>
    Tombstone,

    /// <summary>An active user segment of the same mode overlaps.</summary>
    UserSegment,

    /// <summary>An active introduction overlaps a credits segment.</summary>
    Introduction,
}
