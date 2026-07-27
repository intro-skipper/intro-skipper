// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// Outcome of <see cref="IIntroSkipperDatabase.DeleteSegmentAsync"/>. Carries everything
/// <see cref="IIntroSkipperDatabase.UndoDeleteAsync"/> needs to reverse the delete exactly.
/// </summary>
/// <param name="Deleted">
/// Detached snapshot of the row as it was before the delete; <c>null</c> when no
/// deletable row matched (unknown id or already suppressed).
/// </param>
/// <param name="Suppressed">
/// <c>true</c> when the row was tombstoned (automatic segment); <c>false</c> when it
/// was hard-deleted (user segment) or not found.
/// </param>
public sealed record SegmentDeleteResult(DbSegment? Deleted, bool Suppressed);
