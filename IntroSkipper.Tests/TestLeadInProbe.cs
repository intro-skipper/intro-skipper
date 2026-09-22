// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System.Linq;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Data;
using Xunit;
using static IntroSkipper.Tests.LumaWindows;

public class TestLeadInProbe
{
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
        Assert.True(text.TransitionsPerBandRow >= LeadInProbe.TransitionsMinimum);
        Assert.Equal(2, blob.TransitionsPerBandRow);
        Assert.Equal(16, blank.Background);
        Assert.Equal(0, blank.ForegroundFraction);
    }

    [Fact]
    public void Measure_ReadsThePictureRowsOnly()
    {
        // Bars over a quarter of the frame would put the 10th percentile at black; the picture rows
        // alone put it at the picture's own background, and the foreground fraction is of the picture.
        var frame = Letterboxed(Blob(21), barRows: 9);
        var pictureRows = Enumerable.Range(0, Height).Select(y => y is >= 9 and < 27).ToArray();
        var mask = new bool[Width * Height];

        var whole = LeadInProbe.Measure(frame, Width, mask);
        var picture = LeadInProbe.Measure(frame, Width, pictureRows, mask);

        Assert.Equal(16, whole.Background);
        Assert.Equal(21, picture.Background);
        Assert.Equal(16.0 / (18 * Width), picture.ForegroundFraction, 6);
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
    public void PictureRows_ExcludeRowsBlackThroughoutTheWindow()
    {
        var window = Window(0, (1.0, () => Letterboxed(Blob(21), barRows: 9)), (1.0, () => Letterboxed(Blank(16), barRows: 9)));

        var rows = LeadInProbe.PictureRows(window, Level, Tolerance);

        Assert.Equal(Enumerable.Range(0, Height).Select(y => y is >= 9 and < 27), rows);
    }

    [Theory]
    [InlineData(100.0, 102.0, 98.75, 102.75)]
    [InlineData(100.0, 108.0, 98.75, 108.75)]
    [InlineData(100.0, 109.0, double.NaN, double.NaN)]
    public void ProbeWindow_SpansTheKeyframesWithTheRulesMargins(double a, double b, double start, double end)
    {
        var window = LeadInProbe.ProbeWindow(a, b);

        if (double.IsNaN(start))
        {
            Assert.Null(window);
        }
        else
        {
            Assert.Equal((start, end), (window!.Start, window.End));
        }
    }

    [Fact]
    public void Decide_SameForegroundAcrossTheLevelChange_Keeps()
    {
        // Continuity alone: a lit block on a lighter background, then the same block on the level.
        // The block is no lettering, so the text rule cannot be what keeps the prefix.
        var window = Window(0, (1.0, () => Block(21)), (1.5, () => Block(16)));
        Assert.True(LeadInProbe.Measure(Block(21), Width, new bool[Width * Height]).TransitionsPerBandRow < LeadInProbe.TransitionsMinimum);

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        Assert.IsType<LeadInDecision.Keep>(decision);
    }

    [Fact]
    public void Decide_LargeLitRegionAcrossTheChange_DoesNotKeepOnContinuity()
    {
        // A lit region far beyond the size of a page survives the background drop unchanged: the
        // continuity rule does not vouch for it, the text rule sees two edges per row, and the
        // scene starts at the change.
        var window = Window(0, (1.0, () => Rectangle(Blank(21), x: 4, y: 4, width: 56, height: 28)), (1.5, () => Rectangle(Blank(16), x: 4, y: 4, width: 56, height: 28)));

        var decision = LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe: 1.5, Level, Tolerance);

        var trim = Assert.IsType<LeadInDecision.TrimAt>(decision);
        Assert.Equal(1.0, trim.Time, 6);
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

    [Fact]
    public void Decide_LetterboxBarsDoNotPinTheBackground()
    {
        // Bars over a quarter of the frame stay black throughout. Read whole, every frame's 10th
        // percentile is black and the first frame after A would pass for the change; read over the
        // picture rows, the change is where the picture drops.
        var window = Window(0, (1.0, () => Letterboxed(Blob(21), barRows: 9)), (1.5, () => Letterboxed(Blank(16), barRows: 9)));

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
}
