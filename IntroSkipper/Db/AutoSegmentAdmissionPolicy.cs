// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Admission rules for automatic (analysis) segment writes. Overlapping segments are
/// legal stored state: user writes are admitted unconditionally and may overlap
/// anything, and the stored state's only invariants are the exact-range unique index
/// (engine-enforced) and end &gt; start at the write boundary. These rules gate one
/// thing at one door — <c>ReplaceAutoSegmentsAsync</c>, the sole analysis write path:
/// automation may not contradict recorded human intent. A tombstone means "the user
/// deleted this range"; an active user row of the same mode means "the user said
/// this"; the credits-versus-introduction rule is an automation-quality heuristic
/// riding the same gate. Admission-time only: nothing re-validates stored rows on
/// restore, undo or user edits, so none of these rules hold as properties of the
/// stored state.
/// </summary>
internal static class AutoSegmentAdmissionPolicy
{
    /// <summary>
    /// Determines whether two ranges strictly overlap. Touching boundaries do not
    /// overlap. This single predicate backs every admission guard, so boundary
    /// semantics cannot drift between the tombstone, user-row and
    /// credits-versus-introduction axes.
    /// </summary>
    /// <param name="aStartTicks">Start of the first range in ticks.</param>
    /// <param name="aEndTicks">End of the first range in ticks.</param>
    /// <param name="bStartTicks">Start of the second range in ticks.</param>
    /// <param name="bEndTicks">End of the second range in ticks.</param>
    /// <returns><c>true</c> when the ranges strictly overlap.</returns>
    internal static bool Overlaps(long aStartTicks, long aEndTicks, long bStartTicks, long bEndTicks)
        => aStartTicks < bEndTicks && bStartTicks < aEndTicks;
}
