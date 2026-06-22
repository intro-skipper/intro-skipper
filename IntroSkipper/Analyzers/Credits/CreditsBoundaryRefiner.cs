// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.FFmpeg;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Refines credit scene start times by probing the keyframe gap before a candidate.
/// </summary>
/// <remarks>
/// Uses a targeted blackframe scan only when the keyframe gap can affect the configured minimum duration.
/// </remarks>
internal sealed class CreditsBoundaryRefiner(IFFmpegService ffmpegService)
{
    /// <summary>
    /// Refines a scene start time when a targeted FFmpeg probe finds an earlier black transition.
    /// </summary>
    /// <param name="episode">The episode being analyzed.</param>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="scene">The candidate scene to refine.</param>
    /// <param name="sceneChange">The black percentage that marks a transition into credits.</param>
    /// <param name="threshold">The FFmpeg blackframe threshold.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <param name="logRefinedBoundary">The callback invoked when a boundary is refined.</param>
    /// <param name="cancellationToken">The token used to cancel FFmpeg probing.</param>
    /// <returns>The refined scene start time, or the original scene start time when no valid refinement exists.</returns>
    public async Task<double> RefineAsync(
        QueuedEpisode episode,
        List<BlackFrame> frames,
        CreditScene scene,
        int sceneChange,
        int threshold,
        int minimumDuration,
        Action<double, double> logRefinedBoundary,
        CancellationToken cancellationToken)
    {
        var boundary = CreditsBoundaryHelper.FindBoundaryKeyframeTimes(frames, scene);
        if (boundary is null)
        {
            return scene.StartTime;
        }

        var (lastKeyframeTime, firstBlackTime) = boundary.Value;
        if (!CreditsBoundaryHelper.ShouldRefineBoundary(scene, lastKeyframeTime, minimumDuration))
        {
            return scene.StartTime;
        }

        var probeMinimum = CreditsBoundaryHelper.SelectProbeMinimum(frames, scene, sceneChange);
        var probeStart = lastKeyframeTime + episode.CreditsFingerprintStart;
        var probeEnd = firstBlackTime + episode.CreditsFingerprintStart;
        var probeRange = new TimeRange(probeStart, probeEnd);

        var probeFrames = await ffmpegService
            .DetectBlackFramesAsync(episode, probeRange, probeMinimum, threshold, AnalysisMode.Credits, cancellationToken)
            .ConfigureAwait(false);

        if (probeFrames.Length == 0)
        {
            return scene.StartTime;
        }

        var refinedTime = CreditsBoundaryHelper.TryRefineBoundaryTime(probeFrames[0].Time, lastKeyframeTime, scene.StartTime);
        if (refinedTime is null)
        {
            return scene.StartTime;
        }

        logRefinedBoundary(scene.StartTime, refinedTime.Value);
        return refinedTime.Value;
    }
}
