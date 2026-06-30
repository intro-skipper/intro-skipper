// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

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
        if (obj is not TimeRange tr)
        {
            throw new ArgumentException("obj must be a TimeRange");
        }

        return tr.Duration.CompareTo(Duration);
    }

    /// <summary>
    /// Tests if this TimeRange object intersects the provided TimeRange.
    /// The comparison is strictly non-touching: ranges that only meet at a shared endpoint
    /// (for example <c>[0, 5]</c> and <c>[5, 10]</c>) are not treated as intersecting.
    /// </summary>
    /// <param name="other">TimeRange to test against the current range.</param>
    /// <returns>true if the ranges overlap, excluding ranges that merely touch at an endpoint; otherwise false.</returns>
    public bool Intersects(TimeRange other)
    {
        // Two ranges overlap when each one starts before the other ends. Testing only whether one
        // range's endpoints fall strictly inside the other misses the cases where a range fully
        // contains the other (or the two are identical), so compare the spans directly instead.
        return Start < other.End && other.Start < End;
    }
}
