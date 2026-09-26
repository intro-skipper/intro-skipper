// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Data;

/// <summary>
/// Keyframe scan fixtures for tests.
/// </summary>
internal static class BlackFrameFixtures
{
    /// <summary>
    /// Keyframes every <paramref name="step"/> seconds from <paramref name="from"/> to
    /// <paramref name="to"/>. A keyframe inside one of the <paramref name="spans"/>, the first that
    /// holds it, takes that span's black percentage and visual; any other is busy content, not
    /// black. A span's visual may be <see langword="null"/> for a page the visuals decode did not report.
    /// </summary>
    /// <param name="from">The first keyframe time, relative to the scanned window.</param>
    /// <param name="to">The last keyframe time, inclusive.</param>
    /// <param name="step">The keyframe spacing.</param>
    /// <param name="spans">Inclusive time spans with the black percentage and the visual of their keyframes.</param>
    /// <returns>The keyframes, in time order.</returns>
    internal static Keyframe[] Keyframes(double from, double to, double step, params (double From, double To, int Percentage, Func<double, KeyframeVisual?> Visual)[] spans)
        => [.. Enumerable.Range(0, (int)Math.Round((to - from) / step) + 1).Select(i =>
        {
            var time = from + (i * step);
            var index = Array.FindIndex(spans, span => time >= span.From - 1e-9 && time <= span.To + 1e-9);
            return index < 0 ? new Keyframe(time, 0, KeyframeVisuals.Content(time)) : new Keyframe(time, spans[index].Percentage, spans[index].Visual(time));
        })];

    /// <summary>
    /// The two lists the keyframe scan reports for <paramref name="keyframes"/>: one black row per
    /// keyframe with frame numbers by index, and the visuals of the keyframes that have one.
    /// </summary>
    /// <param name="keyframes">The keyframes, in time order.</param>
    /// <returns>The black rows and the visuals.</returns>
    internal static (BlackFrame[] Rows, KeyframeVisual[] Visuals) Scan(IEnumerable<Keyframe> keyframes)
    {
        List<Keyframe> list = [.. keyframes];
        return ([.. list.Select((keyframe, frame) => new BlackFrame(keyframe.Percentage, keyframe.Time, frame))], [.. list.Select(keyframe => keyframe.Visual).OfType<KeyframeVisual>()]);
    }

    /// <summary>
    /// Creates a dense run of keyframes at the given black percentage.
    /// </summary>
    /// <param name="startTime">The first keyframe time, relative to the scanned window.</param>
    /// <param name="endTime">The last keyframe time, inclusive.</param>
    /// <param name="percentage">The black percentage of every keyframe.</param>
    /// <param name="startFrame">The first frame number, or twice the start time when omitted.</param>
    /// <returns>The keyframes.</returns>
    internal static BlackFrame[] CreateDenseFrames(double startTime, double endTime, int percentage, int? startFrame = null)
    {
        var frames = new List<BlackFrame>();
        var frame = startFrame ?? (int)(startTime * 2);
        for (var time = startTime; time <= endTime; time += 0.5)
        {
            frames.Add(new BlackFrame(percentage, time, frame++));
        }

        return [.. frames];
    }

    /// <summary>
    /// One keyframe of the keyframe scan: its black-frame row's time and percentage, and its
    /// visual. The visual carries its own time, so a fixture can put the two clocks apart.
    /// </summary>
    /// <param name="Time">The black-frame row's time.</param>
    /// <param name="Percentage">The black-frame row's percentage.</param>
    /// <param name="Visual">The keyframe's visual, or <see langword="null"/> for a page the visuals decode did not report.</param>
    internal readonly record struct Keyframe(double Time, int Percentage, KeyframeVisual? Visual);
}
