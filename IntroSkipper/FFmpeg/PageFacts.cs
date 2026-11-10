// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Measures one packed 8-bit yuv420p frame as ffmpeg's blackframe and signalstats filters do.
/// </summary>
/// <remarks>
/// A test pins every value against the two filters on real ffmpeg (<c>TestPageFacts</c>), because
/// the analyzer's thresholds and the cache keys derived from page times rest on these exact values.
/// </remarks>
internal static class PageFacts
{
    // floor(hypot(u - 128, v - 128)), signalstats' saturation of a chroma pair, indexed by (u << 8) | v.
    private static readonly byte[] _saturation = BuildSaturation();

    /// <summary>
    /// Measures a frame into its page.
    /// </summary>
    /// <param name="frame">The frame: width by height luma bytes, then both chroma planes of a rounded-up half width by half height.</param>
    /// <param name="previousLuma">Luma of the frame scanned before this one, or empty when nothing precedes it in this filter graph, which makes the frame difference 0.</param>
    /// <param name="width">The frame's width.</param>
    /// <param name="height">The frame's height.</param>
    /// <param name="threshold">The luma under which a pixel counts as black.</param>
    /// <param name="time">The page's time.</param>
    /// <returns>The page.</returns>
    public static KeyframePage Measure(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> previousLuma, int width, int height, int threshold, double time)
    {
        var pixels = width * height;
        var chromaSamples = ((width + 1) / 2) * ((height + 1) / 2);
        var luma = frame[..pixels];

        Span<int> histogram = stackalloc int[256];
        CountLuma(luma, histogram);

        var black = 0U;
        for (var level = 0; level < Math.Min(threshold, histogram.Length); level++)
        {
            black += (uint)histogram[level];
        }

        // blackframe's unsigned 32-bit arithmetic, which wraps at 42.9 million black pixels.
        var percentage = (int)(unchecked(black * 100) / (uint)pixels);

        // lrint's ties to even, of signalstats' fs * 10 / 100. and fs * 90 / 100. Its int fs * 90
        // overflows above 23,860,929 pixels and prints a wrong YHIGH. This one stays correct, the
        // difference docs/credits.md names.
        var lowRank = Math.Round(pixels * 10L / 100.0, MidpointRounding.ToEven);
        var highRank = Math.Round(pixels * 90L / 100.0, MidpointRounding.ToEven);
        int minimum = -1, low = -1, high = -1, maximum = -1;
        var seen = 0L;
        for (var level = 0; level < histogram.Length; level++)
        {
            if (histogram[level] > 0)
            {
                minimum = minimum < 0 ? level : minimum;
                maximum = level;
            }

            seen += histogram[level];
            low = low < 0 && seen >= lowRank ? level : low;
            high = high < 0 && seen >= highRank ? level : high;
        }

        var saturation = TotalSaturation(frame.Slice(pixels, chromaSamples), frame.Slice(pixels + chromaSamples, chromaSamples));
        var difference = previousLuma.IsEmpty ? 0L : AbsoluteDifference(luma, previousLuma);

        return new KeyframePage(
            time,
            percentage,
            new KeyframeVisual(minimum, low, high, maximum, PrintedByFilter(1.0 * saturation / chromaSamples), PrintedByFilter(1.0 * difference / pixels)));
    }

    // Four interleaved histograms, so consecutive bytes of a flat picture do not stall on the
    // store of the same counter.
    private static void CountLuma(ReadOnlySpan<byte> luma, Span<int> histogram)
    {
        Span<int> first = stackalloc int[256];
        Span<int> second = stackalloc int[256];
        Span<int> third = stackalloc int[256];
        Span<int> fourth = stackalloc int[256];

        var i = 0;
        for (; i <= luma.Length - 4; i += 4)
        {
            first[luma[i]]++;
            second[luma[i + 1]]++;
            third[luma[i + 2]]++;
            fourth[luma[i + 3]]++;
        }

        for (; i < luma.Length; i++)
        {
            first[luma[i]]++;
        }

        for (var level = 0; level < histogram.Length; level++)
        {
            histogram[level] = first[level] + second[level] + third[level] + fourth[level];
        }
    }

    private static long TotalSaturation(ReadOnlySpan<byte> u, ReadOnlySpan<byte> v)
    {
        ReadOnlySpan<byte> table = _saturation;
        var total = 0L;
        for (var i = 0; i < u.Length; i++)
        {
            total += table[(u[i] << 8) | v[i]];
        }

        return total;
    }

    private static long AbsoluteDifference(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous)
    {
        var currentVectors = MemoryMarshal.Cast<byte, Vector<byte>>(current);
        var previousVectors = MemoryMarshal.Cast<byte, Vector<byte>>(previous);
        var total = 0L;
        for (var i = 0; i < currentVectors.Length; i++)
        {
            var difference = Vector.Max(currentVectors[i], previousVectors[i]) - Vector.Min(currentVectors[i], previousVectors[i]);
            Vector.Widen(difference, out var lower, out var upper);
            total += Vector.Sum(lower + upper);
        }

        for (var i = currentVectors.Length * Vector<byte>.Count; i < current.Length; i++)
        {
            total += Math.Abs(current[i] - previous[i]);
        }

        return total;
    }

    // signalstats prints these with %g, and the analyzer's thresholds were tuned on the value
    // parsed back, so the six significant digits stay.
    private static double PrintedByFilter(double value)
        => double.Parse(value.ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static byte[] BuildSaturation()
    {
        var table = new byte[65536];
        for (var u = 0; u < 256; u++)
        {
            for (var v = 0; v < 256; v++)
            {
                table[(u << 8) | v] = (byte)MathF.Sqrt(((u - 128) * (u - 128)) + ((v - 128) * (v - 128)));
            }
        }

        return table;
    }
}
