// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Builds credit-scene candidates from keyframe black-frame evidence and optional blackdetect intervals.
/// </summary>
internal static class CreditSceneBuilder
{
    /// <summary>
    /// Detects credit scenes that have enough black-frame density or can become valid after boundary refinement.
    /// </summary>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="minimum">The minimum black percentage that marks a frame as black.</param>
    /// <param name="sceneChange">The black percentage that marks a transition into credits.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <param name="allowBoundaryRefinement">Whether short scenes that can only reach the minimum duration via boundary refinement may be admitted. Set to <see langword="false" /> when refinement is disabled so an unrefinable short scene does not suppress the interval fallback.</param>
    /// <returns>The detected credit scenes.</returns>
    public static List<CreditScene> DetectCreditScenes(List<BlackFrame> frames, int minimum, int sceneChange, int minimumDuration, bool allowBoundaryRefinement = true)
    {
        var minimumDensity = CreditDetectionPolicy.DefaultMinimumBlackFrameDensity;
        var scenes = DetectCreditSceneCandidates(frames, minimum)
            .Where(scene => CreditSceneMetricsCalculator.Calculate(frames, scene, minimum).MeetsDensity(minimumDensity))
            .ToList();
        var merged = MergeNearbyScenes(frames, scenes, minimum, minimumDensity);
        var shifted = ShiftStartsToTransitionFrames(frames, merged, sceneChange);
        return [.. shifted
            .Where(scene => HasMinimumDuration(scene, minimumDuration) ||
                (allowBoundaryRefinement && CanReachMinimumDurationAfterBoundaryRefinement(frames, scene, minimumDuration)))];
    }

    /// <summary>
    /// Detects raw credit-scene candidates before density and duration filtering.
    /// </summary>
    /// <remarks>
    /// Raw candidates intentionally remain available for targeted blackdetect interval support when
    /// adaptive density cannot accept a candidate on keyframe evidence alone.
    /// </remarks>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="minimum">The minimum black percentage that marks a frame as black.</param>
    /// <returns>The raw candidate scenes.</returns>
    public static List<CreditScene> DetectCreditSceneCandidates(List<BlackFrame> frames, int minimum)
    {
        return FindRawScenes(frames, minimum);
    }

    /// <summary>
    /// Promotes raw candidates that are supported by blackdetect intervals.
    /// </summary>
    /// <remarks>
    /// Interval-supported scenes are anchored to the supporting interval start and may extend to the
    /// interval end so sparse keyframe samples do not truncate confirmed black ranges.
    /// </remarks>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="intervals">The blackdetect intervals relative to the credits fingerprint window.</param>
    /// <param name="minimum">The minimum black percentage that marks a frame as black.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <returns>The interval-supported credit scenes.</returns>
    public static List<CreditScene> DetectIntervalSupportedCreditScenes(
        List<BlackFrame> frames,
        IReadOnlyList<BlackInterval> intervals,
        int minimum,
        int minimumDuration)
    {
        if (intervals.Count == 0)
        {
            return [];
        }

        var candidates = DetectCreditSceneCandidates(frames, minimum);
        var scenes = new List<CreditScene>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var interval = FindSupportingInterval(candidate.StartTime, candidate.EndTime, intervals);
            if (interval is null)
            {
                continue;
            }

            var startTime = interval.Start;
            var endTime = Math.Max(candidate.EndTime, interval.End);
            if (!HasMinimumDuration(startTime, endTime, minimumDuration))
            {
                continue;
            }

            scenes.Add(new CreditScene(
                FindStartFrame(frames, candidate, startTime, minimum),
                FindEndFrame(frames, candidate, endTime, minimum),
                startTime,
                endTime));
        }

        return scenes;
    }

    private static List<CreditScene> FindRawScenes(List<BlackFrame> frames, int minimum)
    {
        var scenes = new List<CreditScene>();
        var maximumInRunGap = EstimateMaximumInRunGap(frames);
        BlackFrame? sceneStart = null;
        BlackFrame? lastBlack = null;

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var isBlack = frame.Percentage >= minimum;

            if (!isBlack)
            {
                continue;
            }

            if (sceneStart is null || lastBlack is null)
            {
                sceneStart = frame;
                lastBlack = frame;
                continue;
            }

            if (frame.Time - lastBlack.Time > maximumInRunGap)
            {
                scenes.Add(new CreditScene(sceneStart.Frame, lastBlack.Frame, sceneStart.Time, lastBlack.Time));
                sceneStart = frame;
            }

            lastBlack = frame;
        }

        if (sceneStart is not null && lastBlack is not null)
        {
            scenes.Add(new CreditScene(sceneStart.Frame, lastBlack.Frame, sceneStart.Time, lastBlack.Time));
        }

        return scenes;
    }

    private static List<CreditScene> MergeNearbyScenes(List<BlackFrame> frames, List<CreditScene> scenes, int minimum, double minimumDensity)
    {
        if (scenes.Count <= 1)
        {
            return scenes;
        }

        var merged = new List<CreditScene>(scenes.Count);
        var current = scenes[0];

        for (var i = 1; i < scenes.Count; i++)
        {
            var scene = scenes[i];
            var mergedScene = new CreditScene(current.StartFrame, scene.EndFrame, current.StartTime, scene.EndTime);
            if (scene.StartTime - current.EndTime <= CreditDetectionPolicy.MaximumSceneMergeGapSeconds &&
                CreditSceneMetricsCalculator.Calculate(frames, mergedScene, minimum).MeetsDensity(minimumDensity))
            {
                current = mergedScene;
            }
            else
            {
                merged.Add(current);
                current = scene;
            }
        }

        merged.Add(current);
        return merged;
    }

    private static List<CreditScene> ShiftStartsToTransitionFrames(List<BlackFrame> frames, List<CreditScene> scenes, int sceneChange)
    {
        var finalScenes = new List<CreditScene>(scenes.Count);
        var searchStart = 0;
        foreach (var scene in scenes)
        {
            var startFrame = scene.StartFrame;
            var startTime = scene.StartTime;

            for (var i = searchStart; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame.Frame > scene.EndFrame)
                {
                    break;
                }

                if (frame.Frame >= startFrame)
                {
                    if (searchStart < i)
                    {
                        searchStart = i;
                    }

                    if (frame.Percentage >= sceneChange)
                    {
                        startFrame = frame.Frame;
                        startTime = frame.Time;
                        break;
                    }
                }
            }

            finalScenes.Add(new CreditScene(startFrame, scene.EndFrame, startTime, scene.EndTime));
        }

        return finalScenes;
    }

    private static bool HasMinimumDuration(CreditScene scene, int minimumDuration)
        => HasMinimumDuration(scene.StartTime, scene.EndTime, minimumDuration);

    private static bool HasMinimumDuration(double startTime, double endTime, int minimumDuration)
        => endTime - startTime >= minimumDuration;

    private static bool CanReachMinimumDurationAfterBoundaryRefinement(List<BlackFrame> frames, CreditScene scene, int minimumDuration)
    {
        var boundary = CreditsBoundaryHelper.FindBoundaryKeyframeTimes(frames, scene);
        return boundary is not null && CreditsBoundaryHelper.ShouldRefineBoundary(scene, boundary.Value.LastKeyframeTime, minimumDuration);
    }

    private static int FindStartFrame(List<BlackFrame> frames, CreditScene scene, double startTime, int minimum)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Frame < scene.StartFrame)
            {
                continue;
            }

            if (frame.Frame > scene.EndFrame)
            {
                break;
            }

            if (frame.Time >= startTime && frame.Percentage >= minimum)
            {
                return frame.Frame;
            }
        }

        return scene.EndFrame;
    }

    private static int FindEndFrame(List<BlackFrame> frames, CreditScene scene, double endTime, int minimum)
    {
        var endFrame = scene.StartFrame;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Frame < scene.StartFrame)
            {
                continue;
            }

            if (frame.Frame > scene.EndFrame)
            {
                break;
            }

            if (frame.Time <= endTime && frame.Percentage >= minimum)
            {
                endFrame = frame.Frame;
            }
        }

        return endFrame;
    }

    private static double EstimateMaximumInRunGap(List<BlackFrame> frames)
    {
        if (frames.Count < 2)
        {
            return CreditDetectionPolicy.MaximumSceneMergeGapSeconds;
        }

        var gaps = new List<double>(frames.Count - 1);
        for (var i = 1; i < frames.Count; i++)
        {
            var gap = frames[i].Time - frames[i - 1].Time;
            if (gap > 0)
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count == 0)
        {
            return CreditDetectionPolicy.MaximumSceneMergeGapSeconds;
        }

        gaps.Sort();
        return Math.Min(CreditDetectionPolicy.MaximumSceneMergeGapSeconds, gaps[gaps.Count / 2] * CreditDetectionPolicy.MaximumKeyframeGapMultiplier);
    }

    private static BlackInterval? FindSupportingInterval(
        double firstBlackTime,
        double lastBlackTime,
        IReadOnlyList<BlackInterval> intervals)
    {
        // Multiple intervals can overlap a single candidate. Pick the one that yields the longest
        // supported scene (anchored to interval.Start, extended to max(candidate end, interval.End))
        // so an early short interval does not mask a later interval that satisfies the minimum duration.
        BlackInterval? best = null;
        var bestSpan = double.NegativeInfinity;
        foreach (var interval in intervals)
        {
            if (interval.Start <= lastBlackTime &&
                interval.End >= firstBlackTime - CreditDetectionPolicy.MaximumIntervalToKeyframeGapSeconds)
            {
                var span = Math.Max(lastBlackTime, interval.End) - interval.Start;
                if (span > bestSpan)
                {
                    bestSpan = span;
                    best = interval;
                }
            }
        }

        return best;
    }
}
