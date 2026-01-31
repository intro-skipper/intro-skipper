// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;

namespace IntroSkipper.Data;

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
        var ranges = FindAllContiguous(times, maximumDistance, 0);
        if (ranges.Count == 0)
        {
            return null;
        }

        // Find and return the longest contiguous range.
        ranges.Sort();
        return ranges[0];
    }

    /// <summary>
    /// Finds all contiguous time ranges that meet the minimum duration.
    /// </summary>
    /// <param name="times">Timestamps to search.</param>
    /// <param name="maximumDistance">Maximum distance permitted between contiguous timestamps.</param>
    /// <param name="minimumDuration">Minimum duration for a range to be included.</param>
    /// <returns>List of all contiguous time ranges meeting the minimum duration, sorted by duration descending.</returns>
    internal static List<TimeRange> FindAllContiguous(double[] times, double maximumDistance, double minimumDuration)
    {
        var ranges = new List<TimeRange>();

        if (times.Length == 0)
        {
            return ranges;
        }

        Array.Sort(times);

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

            if (currentRange.Duration >= minimumDuration)
            {
                ranges.Add(new TimeRange(currentRange));
            }

            currentRange = new TimeRange(next, next);
        }

        // Add the final range if it meets the minimum duration
        if (currentRange.Duration >= minimumDuration)
        {
            ranges.Add(new TimeRange(currentRange));
        }

        // Sort by duration descending (longest first)
        ranges.Sort();

        return ranges;
    }

    /// <summary>
    /// Merges overlapping or adjacent time ranges.
    /// </summary>
    /// <param name="ranges">Time ranges to merge.</param>
    /// <param name="maximumGap">Maximum gap between ranges to consider them adjacent and merge.</param>
    /// <returns>Merged time ranges sorted by start time.</returns>
    public static IReadOnlyList<TimeRange> MergeOverlappingRanges(IEnumerable<TimeRange> ranges, double maximumGap = 0)
    {
        var rangeList = new List<TimeRange>(ranges);

        if (rangeList.Count <= 1)
        {
            return rangeList;
        }

        // Sort by start time
        rangeList.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<TimeRange>();
        var current = new TimeRange(rangeList[0].Start, rangeList[0].End);

        for (var i = 1; i < rangeList.Count; i++)
        {
            var next = rangeList[i];

            // Check if ranges overlap or are within the maximum gap
            if (next.Start <= current.End + maximumGap)
            {
                // Extend current range if next extends beyond
                if (next.End > current.End)
                {
                    current.End = next.End;
                }
            }
            else
            {
                // No overlap/adjacency, save current and start new range
                merged.Add(current);
                current = new TimeRange(next.Start, next.End);
            }
        }

        // Add final range
        merged.Add(current);

        return merged;
    }
}
