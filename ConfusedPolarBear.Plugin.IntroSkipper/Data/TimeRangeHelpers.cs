using System;
using System.Collections.Generic;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

/// <summary>
/// Time range helpers.
/// </summary>
public static class TimeRangeHelpers
{
    /// <summary>
    /// Finds the longest contiguous time range.
    /// </summary>
    /// <param name="times">Sorted timestamps to search.</param>
    /// <param name="maximumDistance">Maximum distance permitted between contiguous timestamps.</param>
    /// <returns>The longest contiguous time range (if one was found), or null (if none was found).</returns>
    public static TimeRange? FindContiguous(double[] times, double maximumDistance)
    {
        if (times.Length == 0)
        {
            return null;
        }

        Array.Sort(times);

        var ranges = new List<TimeRange>();
        var currentRange = new TimeRange(times[0], times[0]);

        // For all provided timestamps, check if it is contiguous with its neighbor.
        for (var i = 0; i < times.Length - 1; i++)
        {
            var current = times[i];
            var next = times[i + 1];

            if (next - current <= maximumDistance)
            {
                currentRange.End = next;
                continue;
            }

            ranges.Add(new TimeRange(currentRange));
            currentRange = new TimeRange(next, next);
        }

        // Find and return the longest contiguous range.
        ranges.Sort();

        return (ranges.Count > 0) ? ranges[0] : null;
    }
}
