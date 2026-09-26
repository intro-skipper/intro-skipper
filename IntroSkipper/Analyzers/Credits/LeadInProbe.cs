// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Moves the start of a black scene after its dark grey lead-in back from the first keyframe at the
/// scene's black level over the frames before it that look the same, from a decoded window between
/// the keyframes.
/// </summary>
/// <remarks>
/// Pure over a <see cref="LumaWindow"/>, so the rule is tested without ffmpeg. The keyframe scan
/// nominates: A is the last keyframe lighter than the level, B the first at it, and the policy
/// starts the scene at B. The probe walks back from B's frame while the frame before matches it and
/// starts the scene at the earliest match after A. A match says only that the downscaled luma looks
/// like B's: a change of colour, detail lost to the scaling, or speech over black are outside what
/// the probe sees. Anything else between the keyframes stops the walk: a subject still moving over
/// a background that has gone black, a last shot, a page that scrolls or fades in. The scene then
/// starts after it, or at B. A frame matches when every pixel is within the black level tolerance
/// of B's: on the measured episodes the frames between the cut and B match exactly, and a page an
/// encoder codes again between keyframes, differing at the edges of its glyphs, leaves the start at
/// B. The probe never moves the start before the frame after A, and never keeps the lead-in.
/// </remarks>
internal static class LeadInProbe
{
    // The window is decoded at this width. A small frame shows a change of picture as well as a
    // large one and keeps the decode's output far under its byte cap for every frame shape and rate.
    internal const int Width = 160;

    // The window runs from A to a quarter second past B, so the frame at B is decoded at any frame
    // rate.
    internal const double LookAheadPadding = 0.25;

    // Frame times sit on the keyframe scan's timeline when the decode seeks to one of its keyframes;
    // this absorbs the rounding of the times the two ffmpeg runs print.
    private const double TimeTolerance = 0.002;

    /// <summary>
    /// The window to decode for a nominated boundary.
    /// </summary>
    /// <param name="lastLighterKeyframe">A, in media time.</param>
    /// <param name="firstLevelKeyframe">B, in media time.</param>
    /// <returns>From A to <see cref="LookAheadPadding"/> after B.</returns>
    internal static TimeRange ProbeWindow(double lastLighterKeyframe, double firstLevelKeyframe)
        => new(lastLighterKeyframe, firstLevelKeyframe + LookAheadPadding);

    /// <summary>
    /// Finds where the scene starts: the earliest frame after A from which every frame up to B
    /// matches B's frame.
    /// </summary>
    /// <param name="window">The decoded window, covering (A, B].</param>
    /// <param name="lastLighterKeyframe">A, in media time.</param>
    /// <param name="firstLevelKeyframe">B, in media time.</param>
    /// <param name="tolerance">How far a pixel may differ from B's and still match, in luma levels.</param>
    /// <returns>
    /// The media time of that frame, or <see langword="null"/> to keep the start at B: when the
    /// frame before B does not match it, or when B's frame was not decoded.
    /// </returns>
    internal static double? Start(LumaWindow window, double lastLighterKeyframe, double firstLevelKeyframe, double tolerance)
    {
        var times = window.Times;
        var b = -1;
        while (b + 1 < times.Count && times[b + 1] <= firstLevelKeyframe + TimeTolerance)
        {
            b++;
        }

        if (b < 0 || times[b] < firstLevelKeyframe - TimeTolerance)
        {
            return null;
        }

        var reference = window.Frame(b);
        var start = b;
        while (start > 0 && times[start - 1] > lastLighterKeyframe + TimeTolerance && Matches(window.Frame(start - 1), reference, tolerance))
        {
            start--;
        }

        return start < b ? times[start] : null;
    }

    private static bool Matches(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> reference, double tolerance)
    {
        for (var i = 0; i < frame.Length; i++)
        {
            if (Math.Abs(frame[i] - reference[i]) > tolerance)
            {
                return false;
            }
        }

        return true;
    }
}
