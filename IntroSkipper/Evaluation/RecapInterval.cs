// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace IntroSkipper.Evaluation;

/// <summary>
/// A closed time interval, in seconds, used by the recap evaluation harness.
/// This is deliberately decoupled from <see cref="Data.Segment"/> and
/// <see cref="Data.TimeRange"/> so the metric math has no dependency on the
/// production analysis types and can be exercised without real media.
/// </summary>
internal readonly record struct RecapInterval
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecapInterval"/> struct.
    /// </summary>
    /// <param name="start">Interval start, in seconds.</param>
    /// <param name="end">Interval end, in seconds.</param>
    public RecapInterval(double start, double end)
    {
        Start = start;
        End = end;
    }

    /// <summary>
    /// Gets an empty interval that represents "no detection".
    /// </summary>
    public static RecapInterval Empty => new(0.0, 0.0);

    /// <summary>
    /// Gets the interval start, in seconds.
    /// </summary>
    public double Start { get; init; }

    /// <summary>
    /// Gets the interval end, in seconds.
    /// </summary>
    public double End { get; init; }

    /// <summary>
    /// Gets the interval duration, in seconds. May be zero or negative for an empty interval.
    /// </summary>
    public double Duration => End - Start;

    /// <summary>
    /// Gets a value indicating whether the interval describes a real, positive-length span.
    /// Mirrors <see cref="Data.Segment.Valid"/> semantics closely enough for evaluation.
    /// </summary>
    public bool HasValue => End > Start;

    /// <inheritdoc/>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"[{Start:F2}s, {End:F2}s]");
}
