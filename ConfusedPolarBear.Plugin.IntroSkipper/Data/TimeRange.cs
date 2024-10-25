// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

#pragma warning disable CA1036 // Override methods on comparable types

/// <summary>
/// Range of contiguous time.
/// </summary>
public class TimeRange : IComparable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimeRange"/> class.
    /// </summary>
    public TimeRange()
    {
        Start = 0;
        End = 0;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeRange"/> class.
    /// </summary>
    /// <param name="start">Time range start.</param>
    /// <param name="end">Time range end.</param>
    public TimeRange(double start, double end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeRange"/> class.
    /// </summary>
    /// <param name="original">Original TimeRange.</param>
    public TimeRange(TimeRange original)
    {
        Start = original.Start;
        End = original.End;
    }

    /// <summary>
    /// Gets or sets the time range start (in seconds).
    /// </summary>
    public double Start { get; set; }

    /// <summary>
    /// Gets or sets the time range end (in seconds).
    /// </summary>
    public double End { get; set; }

    /// <summary>
    /// Gets the duration of this time range (in seconds).
    /// </summary>
    public double Duration => End - Start;

    /// <summary>
    /// Compare TimeRange durations.
    /// </summary>
    /// <param name="obj">Object to compare with.</param>
    /// <returns>int.</returns>
    public int CompareTo(object? obj)
    {
        if (!(obj is TimeRange tr))
        {
            throw new ArgumentException("obj must be a TimeRange");
        }

        return tr.Duration.CompareTo(Duration);
    }

    /// <summary>
    /// Tests if this TimeRange object intersects the provided TimeRange.
    /// </summary>
    /// <param name="tr">Second TimeRange object to test.</param>
    /// <returns>true if tr intersects the current TimeRange, false otherwise.</returns>
    public bool Intersects(TimeRange tr)
    {
        return
            (Start < tr.Start && tr.Start < End) ||
            (Start < tr.End && tr.End < End);
    }
}

#pragma warning restore CA1036

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
