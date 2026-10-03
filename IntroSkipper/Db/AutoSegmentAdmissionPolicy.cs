// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Admission rules for automatic segment writes: automation may not contradict recorded
/// human intent. A tombstone means "the user deleted this range"; an active user row of
/// the same mode means "the user said this"; the credits-versus-introduction rule is an
/// automation-quality heuristic riding the same gate. Two doors apply it:
/// <c>ReplaceAutoSegmentsAsync</c>, the sole analysis write path, and the legacy
/// importer's automatic rows.
/// </summary>
/// <remarks>
/// Overlapping segments are legal stored state: user writes are admitted unconditionally
/// and may overlap anything, and the stored state's only invariants are the exact-range
/// unique index (engine-enforced) and end &gt; start at the write boundary. The rules
/// hold at admission only: nothing re-validates stored rows on restore, undo or user
/// edits, so none of them is a property of the stored state.
/// </remarks>
internal static class AutoSegmentAdmissionPolicy
{
    /// <summary>
    /// Decides whether an automatic segment may be written over an item's stored rows.
    /// Ranges block only when they strictly overlap: touching boundaries do not, a single
    /// shared tick does.
    /// </summary>
    /// <param name="mode">The candidate's mode.</param>
    /// <param name="startTicks">The candidate's start.</param>
    /// <param name="endTicks">The candidate's end.</param>
    /// <param name="itemRows">The item's stored rows in any state. Rows of another mode
    /// count only as introductions against a credits candidate; omit them to skip that
    /// rule.</param>
    /// <returns>The first kind of row that refuses the candidate, checked in the order
    /// tombstone, user segment, introduction; <see cref="AutoSegmentRejection.None"/>
    /// when the candidate is admitted.</returns>
    internal static AutoSegmentRejection Check(AnalysisMode mode, long startTicks, long endTicks, IReadOnlyCollection<DbSegment> itemRows)
    {
        bool Overlapping(DbSegment row) => startTicks < row.EndTicks && row.StartTicks < endTicks;

        if (itemRows.Any(row => row.Type == mode && row.State == SegmentState.Suppressed && Overlapping(row)))
        {
            return AutoSegmentRejection.Tombstone;
        }

        if (itemRows.Any(row => row.Type == mode && row.State == SegmentState.Active && row.Source == SegmentSource.User && Overlapping(row)))
        {
            return AutoSegmentRejection.UserSegment;
        }

        return mode == AnalysisMode.Credits
            && itemRows.Any(row => row.Type == AnalysisMode.Introduction && row.State == SegmentState.Active && Overlapping(row))
            ? AutoSegmentRejection.Introduction
            : AutoSegmentRejection.None;
    }
}
