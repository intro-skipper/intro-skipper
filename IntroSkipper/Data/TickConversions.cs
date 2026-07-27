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

        var scaled = Math.Round(seconds * TimeSpan.TicksPerSecond);
        if (scaled > long.MaxValue)
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
    /// Converts ticks to seconds.
    /// </summary>
    /// <param name="ticks">Time in ticks.</param>
    /// <returns>Time in seconds.</returns>
    internal static double ToSeconds(long ticks) => ticks / (double)TimeSpan.TicksPerSecond;
}
