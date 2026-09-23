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
    public void Background_ReadsThePictureRowsOnly()
    {
        // Bars over half the frame put the whole frame's 10th percentile at black; the picture rows
        // alone put it at the picture's own background.
        var frame = Letterboxed(Blob(21), barRows: 9);
        var pictureRows = Enumerable.Range(0, Height).Select(y => y is >= 9 and < 27).ToArray();

        Assert.Equal(16, LeadInProbe.Background(frame, Width, AllRows(Height)));
        Assert.Equal(21, LeadInProbe.Background(frame, Width, pictureRows));
    }

    [Fact]
    public void PictureRows_ExcludeRowsBlackThroughoutTheWindow()
    {
        var window = Window(0, (1.0, () => Letterboxed(Blob(21), barRows: 9)), (1.0, () => Letterboxed(Blank(16), barRows: 9)));

        var rows = LeadInProbe.PictureRows(window, Level, Tolerance);

        Assert.Equal(Enumerable.Range(0, Height).Select(y => y is >= 9 and < 27), rows);
    }

    [Fact]
    public void PictureRows_KeepTheBlackSpacingBetweenLines()
    {
        // The rows between the text bands never rise above the level, yet they are the page, not
        // bars: only the bands outside the first and last lit row leave the picture.
        var window = Window(0, (1.0, () => Letterboxed(TextRows(16), barRows: 4)));

        var rows = LeadInProbe.PictureRows(window, Level, Tolerance);

        Assert.Equal(Enumerable.Range(0, Height).Select(y => y is >= 6 and < 27), rows);
    }

    [Fact]
    public void PictureRows_IgnoreAStrayBrightPixelInABar()
    {
        // One pixel of noise in a bar row of one frame does not make the row picture; a row where
        // two percent of the pixels rise above the level does.
        var noisy = Letterboxed(Blob(21), barRows: 9);
        noisy[(2 * Width) + 10] = 40;
        var window = Window(0, (0.5, () => noisy), (0.5, () => Letterboxed(Blob(21), barRows: 9)));

        var rows = LeadInProbe.PictureRows(window, Level, Tolerance);

        Assert.Equal(Enumerable.Range(0, Height).Select(y => y is >= 9 and < 27), rows);
    }

    [Fact]
    public void ProbeWindow_SpansTheKeyframesWithTheRuleMargins()
    {
        var window = LeadInProbe.ProbeWindow(100, 104);

        Assert.Equal((100 - LeadInProbe.LookBackPadding, 104 + LeadInProbe.LookAheadPadding), (window.Start, window.End));
    }

    [Fact]
    public void Decide_LitObjectThenBlankBlack_TrimsAtTheLevelChange()
    {
        var window = Window(0, (1.0, () => Blob(21)), (1.5, () => Blank(16)));

        var decision = Decide(window);

        var trim = Assert.IsType<LeadInDecision.TrimAt>(decision);
        Assert.Equal(1.0, trim.Time, 6);
    }

    [Fact]
    public void Decide_LetteredPrefix_TrimsAtTheLevelChange()
    {
        // Lettering on a lighter background, then lettering on the level: the lighter prefix may be
        // credits, but the probe never keeps a lead-in, so the scene starts where the level changes.
        var window = Window(0, (1.0, () => TextRows(21)), (1.5, () => TextRows(16)));

        var decision = Decide(window);

        var trim = Assert.IsType<LeadInDecision.TrimAt>(decision);
        Assert.Equal(1.0, trim.Time, 6);
    }

    [Fact]
    public void Decide_LetterboxBarsDoNotPinTheBackground()
    {
        // Bars over half the frame stay black throughout. Read whole, every frame's 10th percentile
        // is black, so the background never crosses and there is no change to trim to; read over
        // the picture rows, the change is where the picture drops.
        var window = Window(0, (1.0, () => Letterboxed(Blob(21), barRows: 9)), (1.5, () => Letterboxed(Blank(16), barRows: 9)));

        var decision = Decide(window);

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

        var decision = Decide(window);

        var trim = Assert.IsType<LeadInDecision.TrimAt>(decision);
        Assert.Equal(expectedStart, trim.Time, 6);
    }

    [Fact]
    public void Decide_StabilityComesFromTimestampsNotFrameCounts()
    {
        // An irregular frame rate: inside the half second after the change, one frame above the
        // level lasts 20 ms among seven at the level. Weighted by time it is four percent and the
        // change stands; counted as one frame in eight it would be over a tenth.
        var frames = Enumerable.Range(0, 12).Select(i => (i / 12.0, Blob(21)))
            .Append((1.0, Blank(16)))
            .Append((1.02, Blank(24)))
            .Concat(Enumerable.Range(0, 18).Select(i => (1.04 + (i / 12.0), Blank(16))));

        var decision = Decide(Window(frames));

        var trim = Assert.IsType<LeadInDecision.TrimAt>(decision);
        Assert.Equal(1.0, trim.Time, 6);
    }

    [Fact]
    public void Decide_BlackBeatBeforeAFinalShot_TrimsWhereTheLevelHoldsToB()
    {
        // The story drops to black for a beat after A, a final shot lifts it again, and the roll
        // starts at B. The beat holds the level for half a second but not up to B, so the scene
        // starts at B.
        var window = Window(0, (1.0, () => Blob(23)), (0.625, () => Blank(16)), (1.375, () => Blob(26)), (0.75, () => TextRows(16)));

        var decision = Decide(window, firstLevelKeyframe: 3.0);

        var trim = Assert.IsType<LeadInDecision.TrimAt>(decision);
        Assert.Equal(3.0, trim.Time, 6);
    }

    [Fact]
    public void Decide_BackgroundAlreadyAtTheLevel_IsInconclusive()
    {
        // A moving lit object over a black background: the 90th percentile nominated the boundary,
        // but the background never crosses to the level, so the first frame after A is not a cut.
        var window = Window(0, (1.0, () => Blob(16)), (1.5, () => Blank(16)));

        var decision = Decide(window);

        Assert.IsType<LeadInDecision.Inconclusive>(decision);
    }

    [Fact]
    public void Decide_WithoutALevelChange_IsInconclusive()
    {
        var window = Window(0, (2.5, () => Blob(21)));

        var decision = Decide(window);

        Assert.IsType<LeadInDecision.Inconclusive>(decision);
    }

    [Fact]
    public void Decide_WithoutHalfASecondAfterTheChange_IsInconclusive()
    {
        var window = Window(0, (1.0, () => Blob(21)), (0.3, () => Blank(16)));

        var decision = Decide(window);

        Assert.IsType<LeadInDecision.Inconclusive>(decision);
    }

    // A at 0.5 s and B at 1.5 s, unless a test moves B.
    private static LeadInDecision Decide(LumaWindow window, double firstLevelKeyframe = 1.5)
        => LeadInProbe.Decide(window, lastLighterKeyframe: 0.5, firstLevelKeyframe, Level, Tolerance);
}
