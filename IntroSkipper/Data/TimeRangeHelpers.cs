// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Time range helpers.
/// </summary>
public static class TimeRangeHelpers
{
    /// <summary>
    /// Finds the longest contiguous time range.
    /// </summary>
    /// <param name="times">Timestamps to search, in ascending order.</param>
    /// <param name="maximumDistance">Maximum distance permitted between contiguous timestamps.</param>
    /// <returns>The longest contiguous time range (if one was found), or null (if none was found).</returns>
    public static TimeRange? FindContiguous(IReadOnlyList<double> times, double maximumDistance)
    {
        if (times.Count == 0)
        {
            return null;
        }

        var currentRange = new TimeRange(times[0], times[0]);
        var bestRange = currentRange;

        // For all provided timestamps, check if it is contiguous with its neighbor.
        for (var i = 0; i < times.Count - 1; i++)
        {
            var current = times[i];
            var next = times[i + 1];

            if (next - current <= maximumDistance)
            {
                currentRange.End = next;
                continue;
            }

            if (currentRange.Duration > bestRange.Duration)
            {
                bestRange = currentRange;
            }

            currentRange = new TimeRange(next, next);
        }

        return (currentRange.Duration > bestRange.Duration) ? currentRange : bestRange;
    }
}
