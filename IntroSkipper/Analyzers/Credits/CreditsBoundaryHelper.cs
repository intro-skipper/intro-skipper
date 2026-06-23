// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Provides pure boundary-refinement rules for credit scene starts.
/// </summary>
internal static class CreditsBoundaryHelper
{
    /// <summary>
    /// Finds the keyframe immediately before a scene and the first keyframe inside it.
    /// </summary>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="scene">The scene whose start boundary may be refined.</param>
    /// <returns>The boundary keyframe times, or <see langword="null" /> when the scene has no preceding keyframe.</returns>
    public static (double LastKeyframeTime, double FirstBlackTime)? FindBoundaryKeyframeTimes(
        List<BlackFrame> frames,
        CreditScene scene)
    {
        double? lastKeyframeTime = null;
        double? firstBlackTime = null;

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];

            if (frame.Time < scene.StartTime)
            {
                lastKeyframeTime = frame.Time;
            }

            if (frame.Time >= scene.StartTime && firstBlackTime is null)
            {
                firstBlackTime = frame.Time;
                break;
            }
        }

        if (lastKeyframeTime is null || firstBlackTime is null)
        {
            return null;
        }

        return (lastKeyframeTime.Value, firstBlackTime.Value);
    }

    /// <summary>
    /// Selects the blackframe threshold for probing the boundary window.
    /// </summary>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="scene">The scene whose start boundary may be refined.</param>
    /// <param name="sceneChange">The black percentage that marks a transition into credits.</param>
    /// <returns>The lower of the scene start frame percentage and the scene-change threshold.</returns>
    public static int SelectProbeMinimum(List<BlackFrame> frames, CreditScene scene, int sceneChange)
    {
        var startFrame = frames.First(frame => frame.Frame == scene.StartFrame);
        return Math.Min(startFrame.Percentage, sceneChange);
    }

    /// <summary>
    /// Determines whether boundary probing can make a scene meet the minimum duration.
    /// </summary>
    /// <param name="scene">The scene whose start boundary may be refined.</param>
    /// <param name="lastKeyframeTime">The keyframe time immediately before the scene.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <returns><see langword="true" /> if a meaningful boundary probe could make the scene long enough; otherwise, <see langword="false" />.</returns>
    public static bool ShouldRefineBoundary(CreditScene scene, double lastKeyframeTime, int minimumDuration)
    {
        var maximumRefinementWindow = scene.StartTime - lastKeyframeTime;
        if (maximumRefinementWindow <= CreditDetectionPolicy.MinimumBoundaryProbeWindow)
        {
            return false;
        }

        var currentDuration = scene.EndTime - scene.StartTime;
        return currentDuration + maximumRefinementWindow >= minimumDuration;
    }

    /// <summary>
    /// Converts a probe hit inside the boundary window into a scene-relative start time.
    /// </summary>
    /// <param name="probeTime">The probe hit time relative to the probed range.</param>
    /// <param name="lastKeyframeTime">The keyframe time immediately before the scene.</param>
    /// <param name="sceneStartTime">The original scene start time.</param>
    /// <returns>The refined start time, or <see langword="null" /> when the probe hit is outside the valid boundary window.</returns>
    public static double? TryRefineBoundaryTime(double probeTime, double lastKeyframeTime, double sceneStartTime)
    {
        var refinedTime = probeTime + lastKeyframeTime;
        return refinedTime <= lastKeyframeTime || refinedTime > sceneStartTime ? null : refinedTime;
    }
}
