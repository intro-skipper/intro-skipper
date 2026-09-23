// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Places the start of a black scene after its dark grey lead-in on the exact frame where the
/// background drops to the scene's black level, from a decoded window between the keyframes.
/// </summary>
/// <remarks>
/// Pure over a <see cref="LumaWindow"/>, so the rule is tested without ffmpeg. The keyframe scan
/// nominates: A is the last keyframe lighter than the level, B the first at it, and the policy
/// starts the scene at B. The probe looks for the level change L in (A, B]: the first frame at the
/// level that follows a frame above it and holds the level for half a second and up to B. It then
/// trims to L, which is never later than B. Without such an L it is inconclusive and the caller
/// keeps B. It never keeps the lead-in: an overlay that stays on screen, a channel logo or a
/// burned-in subtitle, reads like a credit page carried across the cut, and a keep restores the whole
/// lead-in, minutes of dark story on the measured letterboxed episodes.
/// Every temporal rule is weighted by the frames' durations from their timestamps, clipped to the
/// span it measures; the last frame has no observed duration. The bands above the first and below
/// the last row that rises above the level on more than a stray pixel are letterbox bars and leave
/// the background percentile, since they would pin it at black however dim the picture is.
/// </remarks>
internal static class LeadInProbe
{
    // The window is decoded at this width. The rule reads only each frame's background percentile,
    // which a small frame gives as well as a large one, and a small frame keeps the decode's output
    // far under its byte cap for every frame shape and rate.
    internal const int Width = 160;

    // The window starts just before A, so the lighter keyframe itself is decoded as the frame above
    // the level before a cut right after it, and ends far enough after B for the half second of
    // stability a change at B needs.
    internal const double LookBackPadding = 0.25;
    internal const double LookAheadPadding = 0.75;

    // The level must hold for half a second after L and from L to B, on nine tenths of the observed
    // time.
    internal const double StabilitySeconds = 0.5;
    internal const double StabilityMinimumFraction = 0.9;

    // A row carries picture once this share of it rises above the level in some frame: a stray
    // pixel does not, a dim scene does.
    internal const double PictureRowMinimumFraction = 0.02;

    // Frame times sit on the keyframe scan's timeline when the decode seeks to one of its keyframes;
    // this absorbs the rounding of the times the two ffmpeg runs print.
    private const double TimeTolerance = 0.002;

    /// <summary>
    /// The window to decode for a nominated boundary.
    /// </summary>
    /// <param name="lastLighterKeyframe">A, in media time.</param>
    /// <param name="firstLevelKeyframe">B, in media time.</param>
    /// <returns>From <see cref="LookBackPadding"/> before A to <see cref="LookAheadPadding"/> after B.</returns>
    internal static TimeRange ProbeWindow(double lastLighterKeyframe, double firstLevelKeyframe)
        => new(lastLighterKeyframe - LookBackPadding, firstLevelKeyframe + LookAheadPadding);

    /// <summary>
    /// Finds the rows that carry picture: everything between the first and the last row that, in
    /// some frame of the window, has at least <see cref="PictureRowMinimumFraction"/> of its pixels
    /// above the level plus tolerance. The bands outside are letterbox bars. Black rows inside, such
    /// as the spacing between credit lines, stay picture. The fraction keeps a stray bright pixel,
    /// noise or ringing at a bar's edge, from turning a bar into picture.
    /// </summary>
    /// <param name="window">The decoded window.</param>
    /// <param name="blackLevel">The scene's black level on the scan's own scale.</param>
    /// <param name="tolerance">How far above the level a value still counts as black.</param>
    /// <returns>Per row, whether it carries picture.</returns>
    internal static bool[] PictureRows(LumaWindow window, double blackLevel, double tolerance)
    {
        var width = window.Width;
        var picture = new bool[window.Height];
        var minimumPixels = Math.Max(2, (int)Math.Ceiling(width * PictureRowMinimumFraction));
        for (var i = 0; i < window.FrameCount; i++)
        {
            var frame = window.Frame(i);
            for (var y = 0; y < picture.Length; y++)
            {
                if (picture[y])
                {
                    continue;
                }

                var above = 0;
                foreach (var value in frame.Slice(y * width, width))
                {
                    if (value > blackLevel + tolerance)
                    {
                        above++;
                    }
                }

                picture[y] = above >= minimumPixels;
            }
        }

        var first = Array.IndexOf(picture, true);
        if (first >= 0)
        {
            Array.Fill(picture, true, first, Array.LastIndexOf(picture, true) - first + 1);
        }

        return picture;
    }

    /// <summary>
    /// The background of one frame: the 10th percentile luma over its picture rows.
    /// </summary>
    /// <param name="frame">The frame's luma, row by row.</param>
    /// <param name="width">The frame width.</param>
    /// <param name="pictureRows">Per row, whether it carries picture; at least one must.</param>
    /// <returns>The background level.</returns>
    internal static int Background(ReadOnlySpan<byte> frame, int width, ReadOnlySpan<bool> pictureRows)
    {
        Span<int> histogram = stackalloc int[256];
        var pixels = 0;
        for (var y = 0; y < pictureRows.Length; y++)
        {
            if (!pictureRows[y])
            {
                continue;
            }

            foreach (var value in frame.Slice(y * width, width))
            {
                histogram[value]++;
            }

            pixels += width;
        }

        var target = (pixels + 9) / 10;
        var seen = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (seen >= target)
            {
                return value;
            }
        }

        return 255;
    }

    /// <summary>
    /// Decides where the scene starts.
    /// </summary>
    /// <param name="window">The decoded window, covering (A, B] with a margin before and after.</param>
    /// <param name="lastLighterKeyframe">A, in media time.</param>
    /// <param name="firstLevelKeyframe">B, in media time.</param>
    /// <param name="blackLevel">The scene's black level on the scan's own scale.</param>
    /// <param name="tolerance">How far above the level a background still counts as black.</param>
    /// <returns>The frame to start on, or inconclusive.</returns>
    internal static LeadInDecision Decide(LumaWindow window, double lastLighterKeyframe, double firstLevelKeyframe, double blackLevel, double tolerance)
    {
        var count = window.FrameCount;
        if (count < 2)
        {
            return new LeadInDecision.Inconclusive("fewer than two frames decoded");
        }

        var pictureRows = PictureRows(window, blackLevel, tolerance);
        if (Array.IndexOf(pictureRows, true) < 0)
        {
            return new LeadInDecision.Inconclusive("no row rises above the level anywhere in the window");
        }

        var times = window.Times;
        var atLevel = new bool[count];
        for (var i = 0; i < count; i++)
        {
            atLevel[i] = Background(window.Frame(i), window.Width, pictureRows) <= blackLevel + tolerance;
        }

        // L: the first frame after A and not after B whose background is at the level, whose
        // predecessor was above it, and whose level holds for half a second and up to B. Without
        // that crossing nothing changed between the keyframes: the nomination came from a 90th
        // percentile over a background that was already black, and the first frame after A is not a
        // cut. A drop that gives way to picture again before B is a black beat inside the story, not
        // the start of the scene B opens.
        for (var i = 1; i < count; i++)
        {
            if (times[i] > firstLevelKeyframe + TimeTolerance)
            {
                break;
            }

            if (times[i] <= lastLighterKeyframe + TimeTolerance || !atLevel[i] || atLevel[i - 1])
            {
                continue;
            }

            var (observed, held) = Observe(times, atLevel, times[i], times[i] + StabilitySeconds);
            if (observed < StabilitySeconds - TimeTolerance)
            {
                return new LeadInDecision.Inconclusive("the window ends before the level held for half a second");
            }

            var (observedToB, heldToB) = Observe(times, atLevel, times[i], firstLevelKeyframe);
            if (held >= StabilityMinimumFraction * observed && heldToB >= StabilityMinimumFraction * observedToB)
            {
                return new LeadInDecision.TrimAt(times[i]);
            }
        }

        return new LeadInDecision.Inconclusive("no background crossing to the level between the nominated keyframes");
    }

    // Observed time inside [from, to): every frame's span up to the next timestamp, clipped to it,
    // and the part of it from frames at the level. The last frame has no next timestamp and
    // observes nothing.
    private static (double Observed, double AtLevel) Observe(IReadOnlyList<double> times, bool[] atLevel, double from, double to)
    {
        double observed = 0;
        double held = 0;
        for (var i = 0; i < times.Count - 1; i++)
        {
            var seconds = Math.Max(0, Math.Min(times[i + 1], to) - Math.Max(times[i], from));
            observed += seconds;
            if (atLevel[i])
            {
                held += seconds;
            }
        }

        return (observed, held);
    }
}
