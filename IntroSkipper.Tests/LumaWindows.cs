// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Data;

/// <summary>
/// Synthetic luma windows for the lead-in probe: 64 by 36 frames at 24 fps on the limited-range scale.
/// </summary>
internal static class LumaWindows
{
    internal const int Width = 64;
    internal const int Height = 36;
    internal const double Fps = 24;

    /// <summary>Every row carries picture.</summary>
    internal static bool[] AllRows(int height) => [.. Enumerable.Repeat(true, height)];

    /// <summary>A frame of one value.</summary>
    internal static byte[] Blank(byte background)
    {
        var frame = new byte[Width * Height];
        Array.Fill(frame, background);
        return frame;
    }

    /// <summary>A lit square of 4 by 4 in the middle of the frame.</summary>
    internal static byte[] Blob(byte background) => Rectangle(Blank(background), x: 30, y: 16, width: 4, height: 4);

    /// <summary>Four bands of three rows, each a run of two lit pixels every four across the middle: rows of glyphs with black spacing between them.</summary>
    internal static byte[] TextRows(byte background)
    {
        var frame = Blank(background);
        foreach (var band in new[] { 6, 12, 18, 24 })
        {
            for (var y = band; y < band + 3; y++)
            {
                for (var x = 8; x < 56; x += 4)
                {
                    frame[(y * Width) + x] = 235;
                    frame[(y * Width) + x + 1] = 235;
                }
            }
        }

        return frame;
    }

    /// <summary>Lights a rectangle at 235.</summary>
    internal static byte[] Rectangle(byte[] frame, int x, int y, int width, int height)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                frame[(row * Width) + column] = 235;
            }
        }

        return frame;
    }

    /// <summary>Letterbox bars: the top and bottom rows set to black whatever the picture holds.</summary>
    internal static byte[] Letterboxed(byte[] frame, int barRows)
    {
        Array.Fill(frame, (byte)16, 0, barRows * Width);
        Array.Fill(frame, (byte)16, (Height - barRows) * Width, barRows * Width);
        return frame;
    }

    /// <summary>Segments of frames at the fixed rate, one after another from the start time.</summary>
    internal static LumaWindow Window(double start, params (double Seconds, Func<byte[]> Frame)[] segments)
    {
        var frames = new List<(double Time, byte[] Frame)>();
        foreach (var (seconds, frame) in segments)
        {
            var count = (int)Math.Round(seconds * Fps);
            for (var i = 0; i < count; i++)
            {
                frames.Add((start + (frames.Count / Fps), frame()));
            }
        }

        return Window(frames);
    }

    /// <summary>A window from frames with their own times.</summary>
    internal static LumaWindow Window(IEnumerable<(double Time, byte[] Frame)> frames)
    {
        var list = frames.ToList();
        return new LumaWindow(Width, Height, list.SelectMany(f => f.Frame).ToArray(), [.. list.Select(f => f.Time)]);
    }
}
