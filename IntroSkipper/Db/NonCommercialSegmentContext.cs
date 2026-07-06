// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Snapshot of the stored segments relevant to a non-commercial segment write, loaded inside the
/// store's write transaction and handed to the domain layer so persistence decisions (user-provided
/// precedence, credits/intro overlap) stay atomic with the write itself.
/// </summary>
/// <param name="ExistingSegments">Stored segments with the same item ID and analysis mode as the candidate segment.</param>
/// <param name="StoredIntroduction">The stored introduction segment for the item, or <see langword="null"/> when none exists or the candidate itself is an introduction.</param>
public sealed record NonCommercialSegmentContext(
    IReadOnlyList<DbSegment> ExistingSegments,
    DbSegment? StoredIntroduction);
