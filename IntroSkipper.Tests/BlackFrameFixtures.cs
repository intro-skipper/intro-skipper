// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System.Collections.Generic;
using IntroSkipper.Data;

/// <summary>
/// Black-frame scan results for tests: keyframes every half second.
/// </summary>
internal static class BlackFrameFixtures
{
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
}
