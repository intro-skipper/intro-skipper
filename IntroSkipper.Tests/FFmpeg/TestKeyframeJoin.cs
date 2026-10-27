// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Xunit;

/// <summary>
/// The join that pairs the black-frame rows and the keyframe visuals of one keyframe scan into pages.
/// </summary>
public class TestKeyframeJoin
{
    // Each row: the black-frame times, the visual times, and for each black-frame row the index of
    // the visual its page takes, or -1 for none.
    public static TheoryData<double[], double[], int[]> JoinCases => new()
    {
        // All-keyframe material at 120 fps: keyframes 8.3 ms apart each keep a visual of their own.
        { [0, 0.0083, 0.0166], [0, 0.0083, 0.0166], [0, 1, 2] },

        // Each row takes the nearest unused visual, not the first within 10 ms: a visual with no row
        // of its own 8.3 ms before this row's is left out.
        { [0.0083], [0, 0.0083], [1] },

        // No visual is used twice: of two rows either side of one visual, the first takes it.
        { [0, 0.004], [0.002], [0, -1] },

        // A row with no visual within 10 ms has none; 9 ms is within it on either side, 11 ms is not.
        { [5], [4.98, 5.02], [-1] },
        { [5, 6], [4.991, 6.009], [0, 1] },
        { [5, 6], [4.989, 6.011], [-1, -1] },

        // A visual no row takes is dropped.
        { [0, 1], [0, 0.5, 1], [0, 2] },

        // A tie goes to the earlier visual, and the later one is left for the next row.
        { [0.005, 0.015], [0, 0.01], [0, 1] },

        // An ffmpeg without the visuals filters: no page has a visual.
        { [0, 1], [], [-1, -1] },
    };

    [Theory]
    [MemberData(nameof(JoinCases))]
    public void Pages_PairEachRowWithTheNearestUnusedVisual(double[] rowTimes, double[] visualTimes, int[] expected)
    {
        BlackFrame[] rows = [.. rowTimes.Select((time, frame) => new BlackFrame(100, time, frame))];
        KeyframeVisual[] visuals = [.. visualTimes.Select(time => new KeyframeVisual(time, 16, 16, 16, 235, 0, 0))];

        var pages = KeyframeJoin.Pages(rows, visuals);

        Assert.Equal(rows, pages.Select(page => page.Frame));
        Assert.Equal(expected, pages.Select(page => Array.FindIndex(visuals, visual => ReferenceEquals(visual, page.Visual))));
    }
}
