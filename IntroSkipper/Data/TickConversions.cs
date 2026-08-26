// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Conversions between seconds (analyzer and HTTP edge unit) and ticks
/// (storage and Jellyfin <c>MediaSegment</c> unit, 100 ns).
/// </summary>
internal static class TickConversions
{
    /// <summary>
    /// Converts seconds to ticks, rounding to the nearest tick.
    /// </summary>
    /// <param name="seconds">Time in seconds.</param>
    /// <param name="ticks">Time in ticks when the conversion succeeded; 0 otherwise.</param>
    /// <returns><c>false</c> for NaN, infinite, negative or overflowing values.</returns>
    internal static bool TryFromSeconds(double seconds, out long ticks)
    {
        ticks = 0;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return false;
        }

        // long.MaxValue widens to exactly 2^63, which is NOT representable as a long:
        // compare with >= so the boundary value is rejected instead of saturating.
        var scaled = Math.Round(seconds * TimeSpan.TicksPerSecond);
        if (scaled >= (double)long.MaxValue)
        {
            return false;
        }

        ticks = (long)scaled;
        return true;
    }

    /// <summary>
    /// Converts seconds to ticks, throwing when the value is not representable.
    /// </summary>
    /// <param name="seconds">Time in seconds.</param>
    /// <returns>Time in ticks.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN, infinite, negative or overflows.</exception>
    internal static long FromSeconds(double seconds)
        => TryFromSeconds(seconds, out var ticks)
            ? ticks
            : throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Value is not representable as a non-negative tick count.");

    /// <summary>
    /// Whether a tick range is well-formed: a non-negative start with the end strictly
    /// after it. The single home of the range invariant for tick-native edges (the
    /// legacy MediaSegmentsApi shim speaks ticks on the wire); the seconds edge applies
    /// it through <see cref="TryFromSecondsRange"/>.
    /// </summary>
    /// <param name="startTicks">Range start in ticks.</param>
    /// <param name="endTicks">Range end in ticks.</param>
    /// <returns><c>false</c> when the start is negative or the range is empty or inverted.</returns>
    internal static bool IsValidTickRange(long startTicks, long endTicks)
        => startTicks >= 0 && endTicks > startTicks;

    /// <summary>
    /// Converts a seconds range to a tick range. Succeeds only when both boundaries are
    /// representable and the end is strictly after the start.
    /// </summary>
    /// <param name="startSeconds">Range start in seconds.</param>
    /// <param name="endSeconds">Range end in seconds.</param>
    /// <param name="startTicks">Range start in ticks when the conversion succeeded; 0 otherwise.</param>
    /// <param name="endTicks">Range end in ticks when the conversion succeeded; 0 otherwise.</param>
    /// <returns><c>false</c> when either boundary is not representable or the range is empty or inverted.</returns>
    internal static bool TryFromSecondsRange(double startSeconds, double endSeconds, out long startTicks, out long endTicks)
    {
        if (TryFromSeconds(startSeconds, out startTicks)
            && TryFromSeconds(endSeconds, out endTicks)
            && IsValidTickRange(startTicks, endTicks))
        {
            return true;
        }

        startTicks = 0;
        endTicks = 0;
        return false;
    }

    /// <summary>
    /// Converts ticks to seconds.
    /// </summary>
    /// <param name="ticks">Time in ticks.</param>
    /// <returns>Time in seconds.</returns>
    internal static double ToSeconds(long ticks) => ticks / (double)TimeSpan.TicksPerSecond;
}
