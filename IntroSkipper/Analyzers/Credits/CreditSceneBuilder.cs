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
    /// The longest gap, in seconds, that still joins two black keyframes into one run or two
    /// credits candidates into one.
    /// </summary>
    internal const double MaximumSceneMergeGapSeconds = 20;

    private const double DefaultMinimumBlackFrameDensity = 0.50;
    private const double MaximumIntervalToKeyframeGapSeconds = 2.0;

    /// <summary>
    /// Detects credit scenes that have enough black-frame density and meet the minimum duration.
    /// </summary>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="minimum">The minimum black percentage that marks a frame as black.</param>
    /// <param name="sceneChange">The black percentage that marks a transition into credits.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <returns>The detected credit scenes.</returns>
    public static List<CreditScene> DetectCreditScenes(List<BlackFrame> frames, int minimum, int sceneChange, int minimumDuration)
    {
        var scenes = FindRawScenes(frames, minimum)
            .Where(scene => CreditSceneMetricsCalculator.Calculate(frames, scene, minimum).MeetsDensity(DefaultMinimumBlackFrameDensity))
            .ToList();
        var shifted = ShiftStartsToTransitionFrames(frames, scenes, sceneChange);
        return [.. shifted.Where(scene => HasMinimumDuration(scene, minimumDuration))];
    }

    /// <summary>
    /// Promotes the raw runs that blackdetect intervals support.
    /// </summary>
    /// <remarks>
    /// Blackdetect found each interval black throughout, so all the runs one interval supports are
    /// one scene, however many keyframes between them the scan did not count as black. The scene is
    /// anchored to the interval start and may extend to the interval end so sparse keyframe samples
    /// do not truncate confirmed black ranges.
    /// </remarks>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="runs">The raw runs to promote, in time order (see <see cref="FindRawScenes"/>).</param>
    /// <param name="intervals">The blackdetect intervals relative to the credits fingerprint window.</param>
    /// <param name="minimum">The minimum black percentage that marks a frame as black.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <returns>One scene for each interval that supports a run and spans the minimum duration, in time order.</returns>
    public static List<CreditScene> DetectIntervalSupportedCreditScenes(
        List<BlackFrame> frames,
        IReadOnlyList<CreditScene> runs,
        IReadOnlyList<BlackInterval> intervals,
        int minimum,
        int minimumDuration)
    {
        if (intervals.Count == 0)
        {
            return [];
        }

        List<CreditScene> scenes = [];
        foreach (var supported in runs
            .Select(run => (Run: run, Interval: FindSupportingInterval(run.StartTime, run.EndTime, intervals)))
            .Where(candidate => candidate.Interval is not null)
            .GroupBy(candidate => candidate.Interval!, candidate => candidate.Run))
        {
            var (first, last) = (supported.First(), supported.Last());
            var startTime = supported.Key.Start;
            var endTime = Math.Max(last.EndTime, supported.Key.End);
            if (!HasMinimumDuration(startTime, endTime, minimumDuration))
            {
                continue;
            }

            var span = new CreditScene(first.StartIndex, last.EndIndex, first.StartTime, last.EndTime);
            scenes.Add(new CreditScene(
                FindStartIndex(frames, span, startTime, minimum),
                FindEndIndex(frames, span, endTime, minimum),
                startTime,
                endTime));
        }

        return scenes;
    }

    /// <summary>
    /// Finds the raw credit-scene candidates: runs of black keyframes, before density and duration
    /// filtering. Two black keyframes more than <see cref="MaximumSceneMergeGapSeconds"/> apart
    /// break a run; keyframes that are not black between them do not. Callers that cannot accept a
    /// candidate on keyframe evidence alone confirm these against blackdetect intervals.
    /// </summary>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="minimum">The minimum black percentage that marks a frame as black.</param>
    /// <returns>The raw candidate scenes.</returns>
    public static List<CreditScene> FindRawScenes(List<BlackFrame> frames, int minimum)
    {
        var scenes = new List<CreditScene>();
        var sceneStart = -1;
        var lastBlack = -1;

        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].Percentage < minimum)
            {
                continue;
            }

            if (sceneStart < 0)
            {
                sceneStart = i;
            }
            else if (frames[i].Time - frames[lastBlack].Time > MaximumSceneMergeGapSeconds)
            {
                scenes.Add(new CreditScene(sceneStart, lastBlack, frames[sceneStart].Time, frames[lastBlack].Time));
                sceneStart = i;
            }

            lastBlack = i;
        }

        if (sceneStart >= 0)
        {
            scenes.Add(new CreditScene(sceneStart, lastBlack, frames[sceneStart].Time, frames[lastBlack].Time));
        }

        return scenes;
    }

    private static List<CreditScene> ShiftStartsToTransitionFrames(List<BlackFrame> frames, List<CreditScene> scenes, int sceneChange)
    {
        var finalScenes = new List<CreditScene>(scenes.Count);
        foreach (var scene in scenes)
        {
            var shifted = scene;
            for (var i = scene.StartIndex; i <= scene.EndIndex; i++)
            {
                if (frames[i].Percentage >= sceneChange)
                {
                    shifted = scene with { StartIndex = i, StartTime = frames[i].Time };
                    break;
                }
            }

            finalScenes.Add(shifted);
        }

        return finalScenes;
    }

    private static bool HasMinimumDuration(CreditScene scene, int minimumDuration)
        => HasMinimumDuration(scene.StartTime, scene.EndTime, minimumDuration);

    private static bool HasMinimumDuration(double startTime, double endTime, int minimumDuration)
        => endTime - startTime >= minimumDuration;

    private static int FindStartIndex(List<BlackFrame> frames, CreditScene scene, double startTime, int minimum)
    {
        for (var i = scene.StartIndex; i <= scene.EndIndex; i++)
        {
            if (frames[i].Time >= startTime && frames[i].Percentage >= minimum)
            {
                return i;
            }
        }

        return scene.EndIndex;
    }

    private static int FindEndIndex(List<BlackFrame> frames, CreditScene scene, double endTime, int minimum)
    {
        var endIndex = scene.StartIndex;
        for (var i = scene.StartIndex; i <= scene.EndIndex; i++)
        {
            if (frames[i].Time <= endTime && frames[i].Percentage >= minimum)
            {
                endIndex = i;
            }
        }

        return endIndex;
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
                interval.End >= firstBlackTime - MaximumIntervalToKeyframeGapSeconds)
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
