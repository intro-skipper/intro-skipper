// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// One episode's keyframe scan as the black-frame rules read it: the pages in scan order and the
/// black thresholds normalized over them. It answers whether a page is black and which black pages a
/// scene holds, and it builds the scenes from runs of black pages (scene rule 1).
/// </summary>
/// <remarks>
/// Every time is relative to the credits fingerprint start, as the pages' are. A scene's indices are
/// scan positions (see <see cref="CreditScene"/>), not <see cref="BlackFrame.Frame"/>. The scan reads
/// no visual: the rules on visuals are the keyframe analyzer's.
/// </remarks>
/// <param name="pages">The pages, in scan order.</param>
/// <param name="blackMinimum">The black percentage at or above which a page is black.</param>
/// <param name="sceneChange">The black percentage that marks the transition into credits.</param>
internal sealed class KeyframeScan(IReadOnlyList<KeyframePage> pages, int blackMinimum, int sceneChange)
{
    /// <summary>
    /// The longest gap, in seconds, between two pages of one run: two black pages of a black run, or
    /// two card pages of a card run (see <see cref="CardRunFinder"/>).
    /// </summary>
    internal const double MaximumRunGapSeconds = 20;

    private const double DefaultMinimumBlackFrameDensity = 0.50;
    private const double MaximumIntervalToKeyframeGapSeconds = 2.0;
    private const double SparseAverageBlackFrameGapFactor = 0.5;

    // A page within this many seconds of a scene's bounds belongs to the scene: an interval-supported
    // scene starts on blackdetect's clock, and its cut keyframe can sit just before that start on the
    // scan's clock.
    private const double ScenePad = 0.01;

    /// <summary>Gets the pages, in scan order, each with its black-frame row and its visual when it has one.</summary>
    internal IReadOnlyList<KeyframePage> Pages { get; } = pages;

    /// <summary>Gets the black percentage at or above which a page is black.</summary>
    internal int BlackMinimum { get; } = blackMinimum;

    /// <summary>
    /// Reads a keyframe scan with its thresholds normalized over its own pages against the configured
    /// minimum (see <see cref="BlackFrameThresholdHelper.NormalizeThreshold"/>).
    /// </summary>
    /// <param name="pages">The pages the keyframe scan reported, in scan order.</param>
    /// <param name="minimumPercentage">The configured black minimum percentage.</param>
    /// <returns>The scan.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pages"/> is empty.</exception>
    internal static KeyframeScan Normalize(IReadOnlyList<KeyframePage> pages, int minimumPercentage)
    {
        var (minimum, transition) = BlackFrameThresholdHelper.NormalizeThreshold([.. pages.Select(page => page.Frame)], minimumPercentage);
        return new KeyframeScan(pages, minimum, transition);
    }

    /// <summary>
    /// Determines whether a page at the time belongs to the range from start to end, padded by
    /// <see cref="ScenePad"/> on both sides.
    /// </summary>
    /// <param name="start">The range's start.</param>
    /// <param name="end">The range's end.</param>
    /// <param name="time">The page's black-frame time.</param>
    /// <returns><see langword="true"/> if the page belongs to the range; otherwise, <see langword="false"/>.</returns>
    internal static bool InScene(double start, double end, double time)
        => time >= start - ScenePad && time <= end + ScenePad;

    /// <summary>Determines whether the page at a scan position is black: its raw percentage at or above <see cref="BlackMinimum"/>.</summary>
    /// <param name="index">The page's scan position.</param>
    /// <returns><see langword="true"/> if the page is black; otherwise, <see langword="false"/>.</returns>
    internal bool IsBlack(int index) => Pages[index].Frame.Percentage >= BlackMinimum;

    /// <summary>
    /// Enumerates a scene's black pages: every black page in the whole scan that is <see cref="InScene"/>
    /// of the scene's bounds, so a page just before an interval start counts.
    /// </summary>
    /// <param name="scene">The scene, by its time bounds.</param>
    /// <returns>The black pages with their scan positions, in scan order, pages without a visual included.</returns>
    internal IEnumerable<(int Index, KeyframePage Page)> BlackPages(CreditScene scene)
    {
        for (var i = 0; i < Pages.Count; i++)
        {
            if (IsBlack(i) && InScene(scene.StartTime, scene.EndTime, Pages[i].Frame.Time))
            {
                yield return (i, Pages[i]);
            }
        }
    }

    /// <summary>
    /// Builds the black scenes (scene rule 1): runs of black pages that pass the density gate, start
    /// on their transition page and meet the minimum duration. When none does, blackdetect is asked
    /// to confirm the raw runs, and the scenes are the ones its intervals make. When any scene is
    /// sparse, blackdetect is asked about every scene; if its intervals make a scene of any run, the
    /// dense scenes stay and the runs outside them become the scenes their intervals make, and
    /// otherwise the scenes stay as they are.
    /// </summary>
    /// <param name="minimumDuration">The minimum credit duration, in seconds.</param>
    /// <param name="probeIntervals">Runs blackdetect over the ranges of the scenes it is given and returns the intervals found, relative to the credits fingerprint start, or none when the probe is unavailable. Called at most once.</param>
    /// <returns>The scenes, in scan order, in a new list the caller may change.</returns>
    internal async Task<List<CreditScene>> ScenesAsync(int minimumDuration, Func<IReadOnlyList<CreditScene>, Task<BlackInterval[]>> probeIntervals)
    {
        var scenes = DetectCreditScenes(minimumDuration);
        if (scenes.Count == 0)
        {
            var runs = FindRawScenes();
            return runs.Count == 0
                ? []
                : DetectIntervalSupportedCreditScenes(runs, await probeIntervals(runs).ConfigureAwait(false), minimumDuration);
        }

        if (!scenes.Any(scene => IsSparse(scene, minimumDuration)))
        {
            return scenes;
        }

        var intervals = await probeIntervals(scenes).ConfigureAwait(false);
        var allRuns = FindRawScenes();
        if (DetectIntervalSupportedCreditScenes(allRuns, intervals, minimumDuration).Count == 0)
        {
            return scenes;
        }

        var denseScenes = scenes.Where(scene => !IsSparse(scene, minimumDuration)).ToList();
        List<CreditScene> runsOutsideDenseScenes = [.. allRuns.Where(run => !denseScenes.Any(dense => run.StartIndex <= dense.EndIndex && run.EndIndex >= dense.StartIndex))];
        return [.. denseScenes
            .Concat(DetectIntervalSupportedCreditScenes(runsOutsideDenseScenes, intervals, minimumDuration))
            .OrderBy(scene => scene.StartIndex)];
    }

    private static bool HasMinimumDuration(double startTime, double endTime, int minimumDuration)
        => endTime - startTime >= minimumDuration;

    private static BlackInterval? FindSupportingInterval(
        double firstBlackTime,
        double lastBlackTime,
        IReadOnlyList<BlackInterval> intervals)
    {
        // The longest supported scene wins, so an early short interval does not mask a later one that
        // meets the minimum duration.
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

    private List<CreditScene> DetectCreditScenes(int minimumDuration)
    {
        var scenes = FindRawScenes()
            .Where(IsDense)
            .ToList();
        return [.. ShiftStartsToTransitionFrames(scenes).Where(scene => HasMinimumDuration(scene.StartTime, scene.EndTime, minimumDuration))];
    }

    // One scene per interval that supports a run and meets the minimum duration. Blackdetect found
    // the interval black throughout, so every run it supports is one scene, however many pages between
    // them the scan did not count as black, from the interval start to the later of the interval end
    // and the last run's end, so sparse keyframes do not truncate confirmed black.
    private List<CreditScene> DetectIntervalSupportedCreditScenes(IReadOnlyList<CreditScene> runs, IReadOnlyList<BlackInterval> intervals, int minimumDuration)
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
            scenes.Add(new CreditScene(FindStartIndex(span, startTime), FindEndIndex(span, endTime), startTime, endTime));
        }

        return scenes;
    }

    // Runs of black pages, before density and duration filtering. Pages that are not black between
    // two black pages do not break a run.
    private List<CreditScene> FindRawScenes()
    {
        var scenes = new List<CreditScene>();
        var sceneStart = -1;
        var lastBlack = -1;

        for (var i = 0; i < Pages.Count; i++)
        {
            if (!IsBlack(i))
            {
                continue;
            }

            if (sceneStart < 0)
            {
                sceneStart = i;
            }
            else if (Pages[i].Frame.Time - Pages[lastBlack].Frame.Time > MaximumRunGapSeconds)
            {
                scenes.Add(new CreditScene(sceneStart, lastBlack, Pages[sceneStart].Frame.Time, Pages[lastBlack].Frame.Time));
                sceneStart = i;
            }

            lastBlack = i;
        }

        if (sceneStart >= 0)
        {
            scenes.Add(new CreditScene(sceneStart, lastBlack, Pages[sceneStart].Frame.Time, Pages[lastBlack].Frame.Time));
        }

        return scenes;
    }

    private List<CreditScene> ShiftStartsToTransitionFrames(List<CreditScene> scenes)
    {
        var finalScenes = new List<CreditScene>(scenes.Count);
        foreach (var scene in scenes)
        {
            var shifted = scene;
            for (var i = scene.StartIndex; i <= scene.EndIndex; i++)
            {
                if (Pages[i].Frame.Percentage >= sceneChange)
                {
                    shifted = scene with { StartIndex = i, StartTime = Pages[i].Frame.Time };
                    break;
                }
            }

            finalScenes.Add(shifted);
        }

        return finalScenes;
    }

    private int FindStartIndex(CreditScene scene, double startTime)
    {
        for (var i = scene.StartIndex; i <= scene.EndIndex; i++)
        {
            if (Pages[i].Frame.Time >= startTime && IsBlack(i))
            {
                return i;
            }
        }

        return scene.EndIndex;
    }

    private int FindEndIndex(CreditScene scene, double endTime)
    {
        var endIndex = scene.StartIndex;
        for (var i = scene.StartIndex; i <= scene.EndIndex; i++)
        {
            if (Pages[i].Frame.Time <= endTime && IsBlack(i))
            {
                endIndex = i;
            }
        }

        return endIndex;
    }

    private bool IsDense(CreditScene scene)
    {
        var (total, black) = Count(scene);
        return total > 0 && (double)black / total >= DefaultMinimumBlackFrameDensity;
    }

    private bool IsSparse(CreditScene scene, int minimumDuration)
    {
        var (_, black) = Count(scene);
        return black <= 1 || (scene.EndTime - scene.StartTime) / (black - 1) > minimumDuration * SparseAverageBlackFrameGapFactor;
    }

    private (int Total, int Black) Count(CreditScene scene)
    {
        var total = 0;
        var black = 0;

        for (var i = FirstIndexAtOrAfterTime(scene.StartTime); i < Pages.Count; i++)
        {
            if (Pages[i].Frame.Time > scene.EndTime)
            {
                break;
            }

            total++;
            if (IsBlack(i))
            {
                black++;
            }
        }

        return (total, black);
    }

    private int FirstIndexAtOrAfterTime(double time)
    {
        var low = 0;
        var high = Pages.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (Pages[mid].Frame.Time < time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
