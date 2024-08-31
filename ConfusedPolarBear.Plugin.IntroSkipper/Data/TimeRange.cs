using System;

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
