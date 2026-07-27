// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Pure decision rules guarding segment writes. Centralized so the tombstone and
/// overlap semantics live in one place and stay unit-testable.
/// </summary>
internal static class SegmentWritePolicy
{
    /// <summary>
    /// Determines whether two ranges strictly overlap. Touching boundaries do not overlap.
    /// This single rule drives all write guards: an incoming automatic segment is dropped
    /// when it overlaps a tombstone of the same item and mode ("the user deleted the thing
    /// covering that range"), an active user segment of the same mode, or — for automatic
    /// credits — any active introduction.
    /// </summary>
    /// <param name="aStartTicks">Start of the first range in ticks.</param>
    /// <param name="aEndTicks">End of the first range in ticks.</param>
    /// <param name="bStartTicks">Start of the second range in ticks.</param>
    /// <param name="bEndTicks">End of the second range in ticks.</param>
    /// <returns><c>true</c> when the ranges strictly overlap.</returns>
    internal static bool Overlaps(long aStartTicks, long aEndTicks, long bStartTicks, long bEndTicks)
        => aStartTicks < bEndTicks && bStartTicks < aEndTicks;
}
