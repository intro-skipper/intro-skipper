// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Data;
using Xunit;

public class TestCutStart
{
    // A keyframe gap from 10 to 12 s decoded at 24 fps, and the frame difference of the page at 12.
    private const double PageBefore = 10;
    private const double Page = 12;
    private const double PageDifference = 40;

    [Fact]
    public void Window_PageAfterACut_IsTheGapFromThePageBefore()
    {
        var gap = CutStart.Window(Pages((PageBefore, 0), (Page, 2.5)), 1);

        Assert.NotNull(gap);
        Assert.Equal((PageBefore, Page), (gap.Start, gap.End));
    }

    [Theory]
    [InlineData("first page")]
    [InlineData("difference at the minimum")]
    [InlineData("no visual")]
    [InlineData("page before is later")]
    public void Window_PageAfterNoCut_IsNull(string page)
    {
        var pages = page switch
        {
            "first page" => Pages((Page, PageDifference)),
            "difference at the minimum" => Pages((PageBefore, 0), (Page, CutStart.PageDifferenceMinimum)),
            "no visual" => [Pages((PageBefore, 0))[0], new KeyframePage(new BlackFrame(100, Page, 1), null)],
            "page before is later" => Pages((Page + 1, 0), (Page, PageDifference)),
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        Assert.Null(CutStart.Window(pages, pages.Length - 1));
    }

    [Theory]
    [InlineData(PageDifference / 2)]
    [InlineData(PageDifference)]
    public void Locate_FrameCarryingHalfThePagesDifferenceOrMore_IsTheCut(double cut)
    {
        var frames = Gap(frame => frame == 36 ? cut : 1);

        Assert.Equal(PageBefore + 1.5, CutStart.Locate(frames, new TimeRange(PageBefore, Page), PageDifference));
    }

    [Fact]
    public void Locate_ChangeSpreadOverTheGap_KeepsThePage()
    {
        // A fade: every frame carries a little of the change, the largest just under half the page's.
        var frames = Gap(frame => frame == 36 ? (PageDifference / 2) - 0.1 : 2);

        Assert.Null(CutStart.Locate(frames, new TimeRange(PageBefore, Page), PageDifference));
    }

    [Fact]
    public void Locate_LargeDifferenceOnTheGapsFirstFrameOrOnThePage_KeepsThePage()
    {
        // The gap's first frame is the page before, whose difference is to a frame before the gap,
        // and a frame at the page's own time belongs to the page.
        var frames = Gap(frame => frame == 0 ? 100 : 1).Append(new KeyframeVisual(Page, 16, 16, 16, 16, 0, 100)).ToArray();

        Assert.Null(CutStart.Locate(frames, new TimeRange(PageBefore, Page), PageDifference));
    }

    [Fact]
    public void Locate_DecodeNotStartingOnThePageBefore_KeepsThePage()
    {
        // A decode that lands a tenth of a second late is not on the scan's timeline.
        var frames = Gap(frame => frame == 36 ? PageDifference : 1).Where(frame => frame.Time > PageBefore + 0.1).ToArray();

        Assert.Null(CutStart.Locate(frames, new TimeRange(PageBefore, Page), PageDifference));
    }

    [FactSkipFFmpegTests]
    public async Task DecodeCutWindowAsync_CutFrameCarriesTheGapsLargestDifference()
    {
        // Grey that cuts to black at 1.5 s, between the keyframes at 0 and 2 s. The decode starts on
        // the keyframe at 0, where nothing came before, and the frame at the cut differs from the
        // grey before it by far more than any other frame of the gap.
        var path = await FfmpegTestHelpers.CutToBlackClipAsync("0x808080", cut: 1.5, seconds: 4);
        try
        {
            var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "cut", Path = path, Duration = 4 };

            var frames = await FfmpegTestHelpers.CreateFFmpegService().DecodeCutWindowAsync(episode, new TimeRange(0, 2));

            Assert.NotNull(frames);
            Assert.Equal(48, frames.Length);
            Assert.Equal(0, frames[0].Time, 3);
            Assert.Equal(0, frames[0].FrameDifference);
            var cut = frames.MaxBy(frame => frame.FrameDifference)!;
            Assert.Equal(1.5, cut.Time, 3);
            Assert.InRange(cut.FrameDifference, 90, 120);
            Assert.All(frames.Where(frame => frame != cut), frame => Assert.InRange(frame.FrameDifference, 0, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [FactSkipFFmpegTests]
    public async Task DecodeCutWindowAsync_NonzeroExit_ReturnsNull()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "missing", Path = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mkv"), Duration = 2 };

        Assert.Null(await FfmpegTestHelpers.CreateFFmpegService().DecodeCutWindowAsync(episode, new TimeRange(0, 1)));
    }

    private static KeyframePage[] Pages(params (double Time, double Difference)[] pages)
        => [.. pages.Select((page, frame) => new KeyframePage(new BlackFrame(100, page.Time, frame), KeyframeVisuals.Black(page.Time) with { FrameDifference = page.Difference }))];

    // The 48 frames of the gap, each with the difference the function gives its index.
    private static KeyframeVisual[] Gap(Func<int, double> difference)
        => [.. Enumerable.Range(0, 48).Select(frame => KeyframeVisuals.Black(PageBefore + (frame / 24.0)) with { FrameDifference = difference(frame) })];
}
