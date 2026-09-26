// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Pairs the two lists one keyframe scan reports, the black-frame rows and the keyframe visuals,
/// into pages.
/// </summary>
/// <remarks>
/// The blackframe filter and <c>metadata=print</c> format the same pts differently, so a keyframe's
/// two times can differ. From FFmpeg 7.0 on they agree to under a microsecond. Before 7.0,
/// <c>metadata=print</c> prints six significant digits of the time since the seek, so the two differ
/// by up to 0.5 ms under 1000 s and 5 ms under 10000 s. The two cache rows can also come from
/// separate decodes, when the visuals row was decoded again after the black-frame row.
/// </remarks>
internal static class KeyframeJoin
{
    /// <summary>How far apart a keyframe's black-frame time and visual time may be.</summary>
    internal const double Tolerance = 0.01;

    /// <summary>
    /// Pairs each black-frame row with a visual, one page per row in row order. Rows take visuals in
    /// time order, each the nearest unused visual within <see cref="Tolerance"/>, the earlier one on a
    /// tie, so keyframes that sit that close together each keep their own visual while the two clocks
    /// agree to within half the keyframe spacing. A row with no such visual has none, and a visual no
    /// row takes is dropped.
    /// </summary>
    /// <param name="rows">The black-frame rows, ordered by time.</param>
    /// <param name="visuals">The keyframe visuals, ordered by time.</param>
    /// <returns>The pages, one per row.</returns>
    internal static KeyframePage[] Pages(IReadOnlyList<BlackFrame> rows, IReadOnlyList<KeyframeVisual> visuals)
    {
        var used = new bool[visuals.Count];
        var pages = new KeyframePage[rows.Count];
        var first = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var time = rows[i].Time;

            // A visual that is taken, or too early for this row, is out of reach for every row after it.
            while (first < visuals.Count && (used[first] || time - visuals[first].Time > Tolerance))
            {
                first++;
            }

            var nearest = -1;
            for (var j = first; j < visuals.Count && visuals[j].Time - time <= Tolerance; j++)
            {
                if (!used[j] && (nearest < 0 || Math.Abs(visuals[j].Time - time) < Math.Abs(visuals[nearest].Time - time)))
                {
                    nearest = j;
                }
            }

            if (nearest >= 0)
            {
                used[nearest] = true;
            }

            pages[i] = new KeyframePage(rows[i], nearest < 0 ? null : visuals[nearest]);
        }

        return pages;
    }
}
