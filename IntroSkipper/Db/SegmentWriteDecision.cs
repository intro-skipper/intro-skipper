// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Pure decision logic for non-commercial segment writes. Extracted from
/// <see cref="IntroSkipperDatabase.UpdateTimestampAsync"/> — its only production
/// caller — so the two write-guarding invariants can be unit-tested as a decision
/// table without a database:
/// <list type="number">
/// <item><description>User precedence: an analysis result never overwrites a user-provided segment.</description></item>
/// <item><description>Overlap guard: auto-detected credits must not overlap the stored introduction
/// (strict <c>&lt;</c> comparisons — segments that merely touch at a boundary do not overlap).</description></item>
/// </list>
/// The caller remains responsible for transactional shape (evaluating the decision
/// between the read and the write inside one transaction) and for logging skips.
/// </summary>
internal static class SegmentWriteDecision
{
    /// <summary>
    /// Outcome of <see cref="ShouldPersist"/>. The skip reasons are distinct so the
    /// caller can preserve the existing logging behavior (only the overlap skip logs).
    /// </summary>
    internal enum Verdict
    {
        /// <summary>The new segment replaces any existing segments of the same mode.</summary>
        Persist,

        /// <summary>Skipped silently: an analysis result may not overwrite a user-provided segment.</summary>
        SkipUserProvidedPrecedence,

        /// <summary>Skipped with a log entry: the auto-detected credits overlap the stored introduction.</summary>
        SkipCreditsOverlapIntro,
    }

    /// <summary>
    /// Returns whether the decision needs the stored introduction row. The caller uses
    /// this to fetch the introduction only when the credits/intro overlap guard can
    /// actually apply, instead of on every write.
    /// </summary>
    /// <param name="mode">Analysis mode of the segment being written.</param>
    /// <param name="isUserProvided">Whether the segment was provided by the user.</param>
    /// <returns><see langword="true"/> when the write is an auto-detected credits segment.</returns>
    internal static bool RequiresStoredIntroduction(AnalysisMode mode, bool isUserProvided)
        => mode == AnalysisMode.Credits && !isUserProvided;

    /// <summary>
    /// Decides whether a non-commercial segment write may proceed.
    /// </summary>
    /// <param name="existingSegments">Segments already stored for the same item and mode.</param>
    /// <param name="storedIntroduction">
    /// The stored introduction row, or <see langword="null"/> when none exists or when
    /// <see cref="RequiresStoredIntroduction"/> is <see langword="false"/> (the overlap
    /// guard only evaluates it for auto-detected credits writes).
    /// </param>
    /// <param name="newSegment">Segment being written.</param>
    /// <param name="mode">Analysis mode of the segment being written. Must not be
    /// <see cref="AnalysisMode.Commercial"/> — commercial writes append and never take this path.</param>
    /// <param name="isUserProvided">Whether the segment was provided by the user.</param>
    /// <returns>The <see cref="Verdict"/> for this write.</returns>
    internal static Verdict ShouldPersist(
        IReadOnlyCollection<DbSegment> existingSegments,
        DbSegment? storedIntroduction,
        Segment newSegment,
        AnalysisMode mode,
        bool isUserProvided)
    {
        Debug.Assert(
            mode != AnalysisMode.Commercial,
            "ShouldPersist must not be called for AnalysisMode.Commercial; commercial writes append and never take this path.");

        // Do not overwrite a user-provided segment with an analysis result.
        if (!isUserProvided && existingSegments.Any(s => s.IsUserProvided))
        {
            return Verdict.SkipUserProvidedPrecedence;
        }

        // Guard: prevent auto-detected credits from overlapping with the introduction.
        // Strict < on both sides: touching at a boundary is not an overlap.
        if (RequiresStoredIntroduction(mode, isUserProvided)
            && storedIntroduction is not null
            && newSegment.Start < storedIntroduction.End
            && storedIntroduction.Start < newSegment.End)
        {
            return Verdict.SkipCreditsOverlapIntro;
        }

        return Verdict.Persist;
    }
}
