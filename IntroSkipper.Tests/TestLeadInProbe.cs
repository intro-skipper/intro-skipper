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
    private const double Tolerance = 2;

    [Fact]
    public void ProbeWindow_RunsFromAToJustPastB()
    {
        var window = LeadInProbe.ProbeWindow(100, 104);

        Assert.Equal((100, 104 + LeadInProbe.LookAheadPadding), (window.Start, window.End));
    }

    [Fact]
    public void Start_DimShotThenBlankBlack_StartsAtTheCut()
    {
        var window = Window(0, (1.0, () => Blob(21)), (1.5, () => Blank(16)));

        Assert.Equal(1.0, Start(window)!.Value, 6);
    }

    [Fact]
    public void Start_OverlayThroughTheCut_StartsWhereTheBackgroundDrops()
    {
        // A lit square stays in place while the background drops to black: the frames after the drop
        // are B's frame, those before differ on every background pixel.
        var window = Window(0, (1.0, () => Blob(21)), (1.5, () => Blob(16)));

        Assert.Equal(1.0, Start(window)!.Value, 6);
    }

    [Fact]
    public void Start_SubjectStillMovingOverBlack_StartsWhereItLeaves()
    {
        // The background drops to black at 1 s while a lit subject keeps moving until 2 s. The
        // frames with the subject are story, however black their background.
        var frames = Enumerable.Range(0, 24).Select(i => (i / Fps, Blob(21)))
            .Concat(Enumerable.Range(24, 24).Select(i => (i / Fps, Rectangle(Blank(16), x: i, y: 12, width: 8, height: 8))))
            .Concat(Enumerable.Range(48, 24).Select(i => (i / Fps, Blank(16))));

        Assert.Equal(2.0, Start(Window(frames), firstLevelKeyframe: 2.5)!.Value, 6);
    }

    [Fact]
    public void Start_LastShotRightBeforeB_KeepsTheStartAtB()
    {
        // Seven seconds of black after A, then a half-second shot on black up to B: the frame before
        // B differs from it, so the black beat before the shot is not reached.
        var window = Window(0, (1.0, () => Blob(23)), (6.5, () => Blank(16)), (0.5, () => Rectangle(Blank(16), x: 20, y: 12, width: 8, height: 8)), (0.5, () => Blank(16)));

        Assert.Null(Start(window, firstLevelKeyframe: 8.0));
    }

    [Fact]
    public void Start_FramesMatchingBRightAfterA_StartOnTheFrameAfterA()
    {
        var window = Window(0, (2.0, () => Blank(16)));

        Assert.Equal(0.5 + (1 / Fps), Start(window)!.Value, 6);
    }

    [Fact]
    public void Start_BNotDecoded_KeepsTheStartAtB()
    {
        var window = Window(0, (1.0, () => Blob(21)), (0.3, () => Blank(16)));

        Assert.Null(Start(window));
    }

    // A at 0.5 s and B at 1.5 s, unless a test moves B.
    private static double? Start(LumaWindow window, double firstLevelKeyframe = 1.5)
        => LeadInProbe.Start(window, lastLighterKeyframe: 0.5, firstLevelKeyframe, Tolerance);
}
