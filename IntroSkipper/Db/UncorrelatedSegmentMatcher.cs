// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Single home of the rule that matches a Jellyfin segment row with no shared plugin
/// id (rows predating the shared-id scheme, or foreign-provider rows) to its plugin
/// counterpart, shared by the editor's legacy delete dispatch and the external-delete
/// intent so the two entry points cannot diverge.
/// </summary>
internal static class UncorrelatedSegmentMatcher
{
    /// <summary>
    /// Rows mirrored before the shared-id scheme were converted from seconds by
    /// truncation while the legacy import rounds, so the two can sit one tick apart;
    /// this tolerance absorbs that without reintroducing range-level epsilon matching
    /// elsewhere.
    /// </summary>
    internal const long TickTolerance = 1;

    /// <summary>
    /// Finds the plugin counterpart of a Jellyfin row by mode and boundaries. Several
    /// rows can sit inside the tolerance (1-tick-shifted copies of the same
    /// boundaries), so the closest one wins — an exact match beats a shifted copy —
    /// with the id as a deterministic tie-break instead of enumeration order. A
    /// Jellyfin row can drift further from its plugin counterpart when re-analysis or
    /// edits ran while mirroring was off; the legacy DELETE wire matched mode-wide for
    /// non-commercial types, so that is honored where it is unambiguous — exactly one
    /// candidate row of the mode. Commercials (many per item) keep exact matching.
    /// </summary>
    /// <param name="rows">The item's candidate plugin rows (the caller decides the state filter).</param>
    /// <param name="mode">The mode the Jellyfin row maps to.</param>
    /// <param name="startTicks">The Jellyfin row's start ticks.</param>
    /// <param name="endTicks">The Jellyfin row's end ticks.</param>
    /// <param name="allowModeWideFallback">Whether the non-commercial mode-wide fallback may fire. The
    /// fallback is a drift-healing guess that presumes the caller's view of the mirror is current;
    /// callers pass <see langword="false"/> while the item has unapplied projection work, where a
    /// concurrent delete's counterpart may be gone without a trace and guessing could claim a
    /// segment the caller never addressed.</param>
    /// <returns>The counterpart, or <see langword="null"/> when none matches.</returns>
    internal static DbSegment? Find(IReadOnlyList<DbSegment> rows, AnalysisMode mode, long startTicks, long endTicks, bool allowModeWideFallback = true)
    {
        var match = rows
            .Where(s => s.Type == mode
                && Math.Abs(s.StartTicks - startTicks) <= TickTolerance
                && Math.Abs(s.EndTicks - endTicks) <= TickTolerance)
            .OrderBy(s => Math.Abs(s.StartTicks - startTicks) + Math.Abs(s.EndTicks - endTicks))
            .ThenBy(s => s.Id)
            .FirstOrDefault();

        if (match is null && allowModeWideFallback && mode != AnalysisMode.Commercial)
        {
            var modeRows = rows.Where(s => s.Type == mode).ToList();
            if (modeRows.Count == 1)
            {
                match = modeRows[0];
            }
        }

        return match;
    }
}
