// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Data;
using Xunit;

public class TestLeadInProbe
{
    private const int Width = 64;
    private const int Height = 36;
    private const double Fps = 24;
    private const double Level = 16;
    private const double Tolerance = 2;

    [Fact]
    public void Measure_TellsRowsOfGlyphsFromALitObject()
    {
        var mask = new bool[Width * Height];

        var text = LeadInProbe.Measure(TextRows(21, phase: 0), Width, mask);
        var blob = LeadInProbe.Measure(Blob(21), Width, mask);
        var blank = LeadInProbe.Measure(Blank(16), Width, mask);

        Assert.Equal(21, text.Background);
        Assert.InRange(text.ForegroundFraction, LeadInProbe.ForegroundMinimum, LeadInProbe.ForegroundMaximum);
        Assert.True(text.TransitionsPerBandRow >= LeadInProbe.TransitionsThreshold(Width));
        Assert.Equal(2, blob.TransitionsPerBandRow);
        Assert.Equal(16, blank.Background);
        Assert.Equal(0, blank.ForegroundFraction);
    }

    [Fact]
    public void Iou_IsOneForTheSamePageAndZeroForDisjointPages()
    {
        var a = new bool[Width * Height];
        var b = new bool[Width * Height];
        LeadInProbe.Measure(TextRows(21, phase: 0), Width, a);
        LeadInProbe.Measure(TextRows(16, phase: 0), Width, b);
        Assert.Equal(1, LeadInProbe.Iou(a, b));

        LeadInProbe.Measure(TextRows(16, phase: 2), Width, b);
        Assert.Equal(0, LeadInProbe.Iou(a, b));
    }

    [Fact]
    public void Decide_SameForegroundAcrossTheLevelChange_Keeps()
    {
        // Continuity alone: a lit block on a lighter background, then the same block on the level.
        // The block is no lettering, so the text rule cannot be what keeps the prefix.
        var window = Window(0, (1.0, () => Block(21)), (1.5, () => Block(16)));
        Assert.True(LeadInProbe.Measure(Block(21), Width, new bool[Width * Height]).TransitionsPerBandRow < LeadInProbe.TransitionsThreshold(Width));

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        Assert.IsType<LeadInDecision.Keep>(decision);
    }

    [Fact]
    public void Decide_LetteredPrefixWithAPageChange_Keeps()
    {
        // Text alone: the page changes at the level change, so nothing carries over, and the last
        // second before it is rows of glyphs.
        var window = Window(0, (1.0, () => TextRows(21, phase: 0)), (1.5, () => TextRows(16, phase: 2)));

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        Assert.IsType<LeadInDecision.Keep>(decision);
    }

    [Fact]
    public void Decide_LitObjectThenBlankBlack_TrimsAtTheLevelChange()
    {
        var window = Window(0, (1.0, () => Blob(21)), (1.5, () => Blank(16)));

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        var trim = Assert.IsType<LeadInDecision.TrimAt>(decision);
        Assert.Equal(1.0, trim.Time, 6);
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(5, 1.25)]
    public void Decide_StabilityIsWeightedByTime(int excursionFrames, double expectedStart)
    {
        // One frame above the level inside the half second after the change is under a tenth of the
        // observed time and the change stands; five frames are not, and the change moves past them.
        var window = Window(0, (1.0, () => Blob(21)), (1 / Fps, () => Blank(16)), (excursionFrames / Fps, () => Blank(24)), (1.5, () => Blank(16)));

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        var trim = Assert.IsType<LeadInDecision.TrimAt>(decision);
        Assert.Equal(expectedStart, trim.Time, 6);
    }

    [Fact]
    public void Decide_CoverageComesFromTimestampsNotFrameCounts()
    {
        // A prefix decoded at 12 fps covers its second with twelve frames; the text rule reads the
        // time those frames span, not their number.
        var frames = Enumerable.Range(0, 12).Select(i => (i / 12.0, TextRows(21, phase: 0)))
            .Concat(Enumerable.Range(0, 36).Select(i => (1.0 + (i / Fps), TextRows(16, phase: 2))));

        var decision = LeadInProbe.Decide(Window(frames), lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        Assert.IsType<LeadInDecision.Keep>(decision);
    }

    [Fact]
    public void Decide_WithoutHalfASecondAfterTheChange_IsInconclusive()
    {
        var window = Window(0, (1.0, () => Blob(21)), (0.3, () => Blank(16)));

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        Assert.IsType<LeadInDecision.Inconclusive>(decision);
    }

    [Fact]
    public void Decide_WithoutThreeQuartersOfASecondBefore_IsInconclusive()
    {
        // Continuity fails on the blank frame, and the window holds half a second of the prefix:
        // not enough to read it, so the policy keeps the keyframe start rather than trimming.
        var window = Window(0.5, (0.5, () => Blob(21)), (1.5, () => Blank(16)));

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        Assert.IsType<LeadInDecision.Inconclusive>(decision);
    }

    [Fact]
    public void Decide_WithoutALevelChange_IsInconclusive()
    {
        var window = Window(0, (2.5, () => Blob(21)));

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        Assert.IsType<LeadInDecision.Inconclusive>(decision);
    }

    private static byte[] Blank(byte background)
    {
        var frame = new byte[Width * Height];
        Array.Fill(frame, background);
        return frame;
    }

    // A lit square of 8 by 8: 2.8 percent of the frame, two edges per row.
    private static byte[] Block(byte background) => Rectangle(Blank(background), x: 28, y: 14, width: 8, height: 8);

    // A lit square of 4 by 4: 0.7 percent of the frame, two edges per row.
    private static byte[] Blob(byte background) => Rectangle(Blank(background), x: 30, y: 16, width: 4, height: 4);

    // Four bands of three rows, each a run of two lit pixels every four across the middle: 24 edges
    // per row and 12.5 percent of the frame. Phase 2 lights the pixels phase 0 leaves dark.
    private static byte[] TextRows(byte background, int phase)
    {
        var frame = Blank(background);
        foreach (var band in new[] { 6, 12, 18, 24 })
        {
            for (var y = band; y < band + 3; y++)
            {
                for (var x = 8 + phase; x < 56; x += 4)
                {
                    frame[(y * Width) + x] = 235;
                    frame[(y * Width) + x + 1] = 235;
                }
            }
        }

        return frame;
    }

    private static byte[] Rectangle(byte[] frame, int x, int y, int width, int height)
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

    // Segments of frames at the fixed rate, one after another from the start time.
    private static LumaWindow Window(double start, params (double Seconds, Func<byte[]> Frame)[] segments)
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

    private static LumaWindow Window(IEnumerable<(double Time, byte[] Frame)> frames)
    {
        var list = frames.ToList();
        return new LumaWindow(Width, Height, list.SelectMany(f => f.Frame).ToArray(), [.. list.Select(f => f.Time)]);
    }
}
