// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Places the start of a credits part on the cut before its first page: the frame of the keyframe
/// gap before that page that carries most of the change between the two keyframes.
/// </summary>
/// <remarks>
/// Pure over the frames of a cut decode, so the rule is tested without ffmpeg. The keyframe scan
/// nominates: a page follows a cut when its frame difference to the page before is above
/// <see cref="PageDifferenceMinimum"/>. Every frame of the gap between the two keyframes is then
/// decoded, and the part starts at the frame with the largest difference to the frame before it,
/// when that difference is at least <see cref="CutShare"/> of the page's: one frame then carries most
/// of the change. Motion or a fade spreads the change over many frames, and the part keeps its
/// keyframe start. A cut on the keyframe itself leaves no frame of the gap with a large difference,
/// so the part keeps the keyframe, which is the cut.
/// </remarks>
internal static class CutStart
{
    /// <summary>
    /// The frame difference to the page before above which a page follows a cut. The cut from a dim
    /// last shot to black on a measured movie differs by 2.2, and a minimum of 3 or more loses it.
    /// </summary>
    internal const double PageDifferenceMinimum = 2;

    /// <summary>
    /// The share of the page's frame difference the cut frame must carry. On the measured corpus 0.25
    /// skipped 1.9 s more story, and 0.75 lost a start.
    /// </summary>
    internal const double CutShare = 0.5;

    // Frame times sit on the keyframe scan's timeline when the decode seeks to one of its keyframes;
    // this absorbs the rounding of the times the two ffmpeg runs print.
    private const double TimeTolerance = 0.002;

    /// <summary>
    /// The keyframe gap to decode before a part whose first page is the page at the index.
    /// </summary>
    /// <param name="pages">The keyframe scan's pages, ordered by time.</param>
    /// <param name="index">The part's first page.</param>
    /// <returns>From the page before to the page, in the pages' time; <see langword="null"/> when the page is the first, has no visual, or does not follow a cut.</returns>
    internal static TimeRange? Window(IReadOnlyList<KeyframePage> pages, int index)
        => index > 0
            && pages[index].Visual is { FrameDifference: > PageDifferenceMinimum }
            && pages[index - 1].Frame.Time < pages[index].Frame.Time
            ? new TimeRange(pages[index - 1].Frame.Time, pages[index].Frame.Time)
            : null;

    /// <summary>
    /// Finds the cut frame in a decoded keyframe gap.
    /// </summary>
    /// <param name="frames">The cut decode's frames, in time order, on the gap's timeline.</param>
    /// <param name="window">The gap, from the page before to the part's first page (see <see cref="Window"/>).</param>
    /// <param name="pageDifference">The first page's frame difference to the page before.</param>
    /// <returns>
    /// The time of the gap frame with the largest difference, the earliest on a tie, when that
    /// difference is at least <see cref="CutShare"/> of <paramref name="pageDifference"/>; otherwise
    /// <see langword="null"/>, to keep the start on the page. The gap's first frame is the page before,
    /// whose difference is to a frame outside the gap, so it never counts. The start also stays on the
    /// page when the decode does not begin on the page before.
    /// </returns>
    internal static double? Locate(IReadOnlyList<KeyframeVisual> frames, TimeRange window, double pageDifference)
    {
        var gap = frames.Where(frame => frame.Time >= window.Start - TimeTolerance && frame.Time < window.End - TimeTolerance).ToList();
        if (gap.Count == 0 || gap[0].Time > window.Start + TimeTolerance)
        {
            return null;
        }

        var cut = gap.Skip(1).MaxBy(frame => frame.FrameDifference);
        return cut is not null && cut.FrameDifference >= CutShare * pageDifference ? cut.Time : null;
    }
}
