// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Media file analyzer used to detect end credits that consist of text overlaid on a black background.
/// Uses full keyframe scanning with density gating and boundary refinement for robust credit detection.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BlackFrameAltAnalyzer"/> class.
/// </remarks>
/// <param name="logger">Logger for the analyzer.</param>
public sealed partial class BlackFrameAltAnalyzer(ILogger<BlackFrameAltAnalyzer> logger) : IMediaFileAnalyzer
{
    private const int MaximumTimeSkip = 20;
    private const double MinimumBlackFrameDensity = 0.50;
    private const double MinimumBoundaryProbeWindow = 0.50;
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<BlackFrameAltAnalyzer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != AnalysisMode.Credits)
        {
            throw new NotImplementedException($"{nameof(BlackFrameAltAnalyzer)} only supports {nameof(AnalysisMode.Credits)} mode");
        }

        var unanalyzedEpisodes = analysisQueue
            .Where(e => e.NeedsAnalysis(mode))
            .ToList();

        if (unanalyzedEpisodes.Count == 0)
        {
            return analysisQueue;
        }

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config);

        LogAnalyzingEpisodes(unanalyzedEpisodes.Count);

        var minimumPercentage = _config.BlackFrameMinimumPercentage;
        var threshold = _config.BlackFrameThreshold;
        var minimumDuration = _config.MinimumCreditsDuration;
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");

        foreach (var episode in unanalyzedEpisodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var credit = DetectCredits(episode, minimumPercentage, threshold, minimumDuration);

                if (credit is null || !credit.Valid)
                {
                    LogNoValidCreditsFound(episode.Name);
                    continue;
                }

                credit = timeAdjustmentHelper.AdjustIntroTimes(episode, credit);
                LogFoundCredits(episode.Name, credit.Start);

                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                await plugin.UpdateTimestampAsync(credit, mode, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogAnalysisCancelled();
                break;
            }
            catch (Exception ex)
            {
                LogErrorAnalyzingCredits(ex, episode.Name);
            }
        }

        return analysisQueue;
    }

    /// <summary>
    /// Detects the start of blackframe credits from FFmpeg blackframe filter output.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="minimumPercentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <returns>Time range of the detected credits.</returns>
    public Segment? DetectCredits(QueuedEpisode episode, int minimumPercentage, int threshold, int minimumDuration)
    {
        var blackFrames = FFmpegWrapper.DetectBlackFrames(episode, threshold).ToList();

        if (blackFrames.Count == 0)
        {
            return null;
        }

        var (minimum, sceneChange) = NormalizeThreshold(blackFrames, minimumPercentage);
        var scenes = DetectCreditScenes(blackFrames, minimum, sceneChange);
        if (scenes.Count == 0)
        {
            return null;
        }

        // Start from the last scene and work backwards to find the first valid credits segment
        for (var i = scenes.Count - 1; i >= 0; i--)
        {
            var scene = scenes[i];

            // Refine the start time using full-frame boundary probing
            var refinedStartTime = _config.RefineCreditsBoundary
                ? RefineBoundary(episode, blackFrames, scene, sceneChange, threshold, minimumDuration)
                : scene.StartTime;

            var segment = new Segment(episode.EpisodeId, new TimeRange(refinedStartTime + episode.CreditsFingerprintStart, scene.EndTime + episode.CreditsFingerprintStart));

            if (segment.Duration >= minimumDuration)
            {
                LogFoundValidCreditsSegment(segment.Start, segment.End, segment.Duration);

                return segment;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes black frame detection thresholds based on the video's natural black levels.
    /// Uses the 1st-percentile frame as a floor (capped at 30%) and scales the minimum
    /// percentage and scene-change threshold accordingly.
    /// </summary>
    /// <param name="frames">All detected keyframes.</param>
    /// <param name="minimumPercentage">Configured minimum black percentage.</param>
    /// <returns>Normalized (minimum, sceneChange) thresholds.</returns>
    internal static (int Minimum, int SceneChange) NormalizeThreshold(List<BlackFrame> frames, int minimumPercentage)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count, nameof(frames));

        var orderedFrames = frames.OrderBy(f => f.Percentage).ToList();
        var percentileIndex = (int)(frames.Count * 0.01); // 1st percentile
        var floor = Math.Min(orderedFrames[percentileIndex].Percentage, 30);
        var minimum = (minimumPercentage * (100 - floor) / 100) + floor;
        var sceneChange = (95 * (100 - floor) / 100) + floor;
        return (minimum, sceneChange);
    }

    /// <summary>
    /// Finds the bounding keyframe times for a credit scene transition.
    /// Returns the time of the immediately preceding keyframe and the first keyframe at or after the scene start.
    /// </summary>
    /// <param name="frames">All detected keyframes.</param>
    /// <param name="scene">The credit scene to find boundaries for.</param>
    /// <returns>Boundary times, or null if no preceding keyframe exists.</returns>
    internal static (double LastKeyframeTime, double FirstBlackTime)? FindBoundaryKeyframeTimes(
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
    /// Refines the start time of a credit scene by probing the gap between keyframes
    /// with full-frame (non-keyframe-only) analysis.
    /// </summary>
    /// <param name="episode">The episode being analyzed.</param>
    /// <param name="frames">All detected keyframes.</param>
    /// <param name="scene">The credit scene whose start time to refine.</param>
    /// <param name="sceneChange">Normalized transition threshold used to select the scene start.</param>
    /// <param name="threshold">Pixel luminance threshold for black detection.</param>
    /// <param name="minimumDuration">Minimum duration required for a credits segment.</param>
    /// <returns>Refined start time in seconds (relative to CreditsFingerprintStart).</returns>
    private double RefineBoundary(
        QueuedEpisode episode,
        List<BlackFrame> frames,
        CreditScene scene,
        int sceneChange,
        int threshold,
        int minimumDuration)
    {
        var boundary = FindBoundaryKeyframeTimes(frames, scene);
        if (boundary is null)
        {
            return scene.StartTime;
        }

        var (lastKeyframeTime, firstBlackTime) = boundary.Value;
        if (!ShouldRefineBoundary(scene, lastKeyframeTime, minimumDuration))
        {
            return scene.StartTime;
        }

        var probeMinimum = SelectProbeMinimum(frames, scene, sceneChange);

        // Probe the gap between the preceding keyframe and the first black keyframe
        // using full-frame analysis (no -skip_frame nokey).
        // Times are relative to CreditsFingerprintStart, so offset for the range-based overload.
        var probeStart = lastKeyframeTime + episode.CreditsFingerprintStart;
        var probeEnd = firstBlackTime + episode.CreditsFingerprintStart;
        var probeRange = new TimeRange(probeStart, probeEnd);

        var probeFrames = FFmpegWrapper.DetectBlackFrames(episode, probeRange, probeMinimum, threshold);

        if (probeFrames.Length == 0)
        {
            return scene.StartTime;
        }

        var refinedTime = TryRefineBoundaryTime(probeFrames[0].Time, lastKeyframeTime, scene.StartTime);
        if (refinedTime is null)
        {
            return scene.StartTime;
        }

        LogRefinedBoundary(scene.StartTime, refinedTime.Value);

        return refinedTime.Value;
    }

    /// <summary>
    /// Selects the minimum black percentage threshold for boundary probing.
    /// </summary>
    /// <remarks>
    /// Probing should not use a weaker threshold than the final scene start decision.
    /// When the scene start was chosen by the stronger transition threshold, probing is capped at that value.
    /// Otherwise, probing uses the selected start frame's measured black level.
    /// </remarks>
    /// <param name="frames">All detected keyframes.</param>
    /// <param name="scene">The credit scene whose start boundary is being refined.</param>
    /// <param name="sceneChange">Normalized transition threshold used to select the scene start.</param>
    /// <returns>The minimum black percentage to use for full-frame probing.</returns>
    internal static int SelectProbeMinimum(List<BlackFrame> frames, CreditScene scene, int sceneChange)
    {
        var startFrame = frames.First(frame => frame.Frame == scene.StartFrame);
        return Math.Min(startFrame.Percentage, sceneChange);
    }

    /// <summary>
    /// Determines whether boundary refinement is likely to materially change the result.
    /// </summary>
    /// <param name="scene">The credit scene whose start boundary is being refined.</param>
    /// <param name="lastKeyframeTime">Time of the preceding keyframe.</param>
    /// <param name="minimumDuration">Minimum duration required for a credits segment.</param>
    /// <returns>
    /// <see langword="true"/> when the preceding keyframe gap is large enough to matter and the
    /// additional time could affect segment validity; otherwise, <see langword="false"/>.
    /// </returns>
    internal static bool ShouldRefineBoundary(CreditScene scene, double lastKeyframeTime, int minimumDuration)
    {
        var maximumRefinementWindow = scene.StartTime - lastKeyframeTime;
        if (maximumRefinementWindow <= MinimumBoundaryProbeWindow)
        {
            return false;
        }

        var currentDuration = scene.EndTime - scene.StartTime;
        return currentDuration + maximumRefinementWindow >= minimumDuration;
    }

    /// <summary>
    /// Converts an FFmpeg probe timestamp (relative to the seek point) back to
    /// CreditsFingerprintStart-relative time.
    /// </summary>
    /// <remarks>
    /// The range-based FFmpeg overload uses -ss before -i (input seeking),
    /// so output timestamps are relative to the seek point (lastKeyframeTime + CreditsFingerprintStart).
    /// Converting back:
    ///   absoluteTime = probeTime + lastKeyframeTime + CreditsFingerprintStart,
    ///   relativeTime = absoluteTime - CreditsFingerprintStart
    ///                = probeTime + lastKeyframeTime.
    /// </remarks>
    /// <param name="probeTime">Timestamp from FFmpeg probe output (relative to seek point).</param>
    /// <param name="lastKeyframeTime">Time of the preceding keyframe (relative to CreditsFingerprintStart).</param>
    /// <returns>Refined start time in seconds (relative to CreditsFingerprintStart).</returns>
    internal static double ConvertProbeTimestamp(double probeTime, double lastKeyframeTime) => probeTime + lastKeyframeTime;

    /// <summary>
    /// Validates a probe timestamp for boundary refinement and converts it to scene-relative time.
    /// </summary>
    /// <param name="probeTime">Timestamp from FFmpeg probe output (relative to seek point).</param>
    /// <param name="lastKeyframeTime">Time of the preceding keyframe.</param>
    /// <param name="sceneStartTime">Current scene start chosen by keyframe analysis.</param>
    /// <returns>
    /// The refined start time when the probe lands strictly after the preceding keyframe and no later than
    /// the current scene start; otherwise <see langword="null"/>.
    /// </returns>
    internal static double? TryRefineBoundaryTime(double probeTime, double lastKeyframeTime, double sceneStartTime)
    {
        var refinedTime = ConvertProbeTimestamp(probeTime, lastKeyframeTime);
        return refinedTime <= lastKeyframeTime || refinedTime > sceneStartTime ? null : refinedTime;
    }

    /// <summary>
    /// Checks whether a scene meets the minimum black-frame density threshold.
    /// </summary>
    /// <remarks>
    /// <paramref name="searchStart"/> is an optimization index passed by reference. It tracks the
    /// first frame index that falls within (or after) the most recently checked scene. Because both
    /// frames and scenes are time-sorted, later scenes can skip frames that precede them.
    ///
    /// <para><b>Invariant:</b> <paramref name="searchStart"/> only advances forward. Each call sets it
    /// to the index of the first frame at or after <c>scene.StartTime</c>, so the next call with a
    /// later scene starts scanning from there instead of from 0.</para>
    ///
    /// <para><b>Exception:</b> Callers checking a <em>merged</em> span (which may start earlier than the
    /// previous individual scene) must pass a fresh <c>searchStart = 0</c> to avoid skipping frames
    /// that fall within the merged range.</para>
    /// </remarks>
    private static bool HasMinimumBlackFrameDensity(List<BlackFrame> frames, CreditScene scene, int minimum, ref int searchStart)
    {
        var totalInScene = 0;
        var blackInScene = 0;
        for (var i = searchStart; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Time > scene.EndTime)
            {
                break; // Frames are time-sorted; no more matches possible
            }

            if (frame.Time >= scene.StartTime)
            {
                if (totalInScene == 0)
                {
                    searchStart = i; // Advance for later scenes that start at or after this one
                }

                totalInScene++;
                if (frame.Percentage >= minimum)
                {
                    blackInScene++;
                }
            }
        }

        return totalInScene > 0 && (double)blackInScene / totalInScene >= MinimumBlackFrameDensity;
    }

    internal static List<CreditScene> DetectCreditScenes(List<BlackFrame> frames, int minimum, int sceneChange)
    {
        var scenes = new List<CreditScene>();
        BlackFrame? sceneStart = null;
        BlackFrame? lastBlack = null;

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var isBlack = frame.Percentage >= minimum;

            // Start new scene
            if (isBlack && sceneStart is null)
            {
                sceneStart = frame;
                lastBlack = frame;
            }

            // Continue scene
            else if (isBlack)
            {
                lastBlack = frame;
            }

            // End scene if gap is too large or we're at the last frame
            else if (sceneStart is not null && lastBlack is not null &&
                    (i == frames.Count - 1 || frame.Frame - lastBlack.Frame > 5))
            {
                if (lastBlack.Frame - sceneStart.Frame >= 5)
                {
                    scenes.Add(new CreditScene(sceneStart.Frame, lastBlack.Frame, sceneStart.Time, lastBlack.Time));
                }

                sceneStart = null;
            }
        }

        // Handle final scene
        if (sceneStart is not null && lastBlack is not null && lastBlack.Frame - sceneStart.Frame >= 5)
        {
            scenes.Add(new CreditScene(sceneStart.Frame, lastBlack.Frame, sceneStart.Time, lastBlack.Time));
        }

        // Density gating: reject scenes where the ratio of black keyframes to total keyframes is too low.
        // First filter individual scenes, then re-check merged spans so long non-black gaps do not
        // turn separate dense scenes into one mostly non-black segment.
        //
        // searchStart advances monotonically across this loop because scenes are time-sorted —
        // each scene starts at or after the previous one, so we never need to revisit earlier frames.
        var densityFiltered = new List<CreditScene>(scenes.Count);
        var searchStart = 0;
        foreach (var scene in scenes)
        {
            if (HasMinimumBlackFrameDensity(frames, scene, minimum, ref searchStart))
            {
                densityFiltered.Add(scene);
            }
        }

        if (densityFiltered.Count == 0)
        {
            return densityFiltered;
        }

        // Merge density-filtered scenes that are close together
        List<CreditScene> merged;
        if (densityFiltered.Count <= 1)
        {
            merged = densityFiltered;
        }
        else
        {
            merged = new List<CreditScene>(densityFiltered.Count);
            var current = densityFiltered[0];

            for (var i = 1; i < densityFiltered.Count; i++)
            {
                var scene = densityFiltered[i];
                var mergedScene = new CreditScene(current.StartFrame, scene.EndFrame, current.StartTime, scene.EndTime);

                // Fresh searchStart: the merged span reaches back to current.StartTime, which may
                // precede the index left by the per-scene density pass. Restarting from 0 ensures
                // no frames in the merged range are skipped.
                var mergeSearchStart = 0;
                if (scene.StartTime - current.EndTime <= MaximumTimeSkip &&
                    HasMinimumBlackFrameDensity(frames, mergedScene, minimum, ref mergeSearchStart))
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
        }

        // Find the transition frame for each merged scene.
        // searchStart is reset to 0 and advances monotonically — merged scenes are frame-sorted,
        // so each scene's startFrame is at or after the previous one's.
        var finalScenes = new List<CreditScene>(merged.Count);
        searchStart = 0;
        foreach (var scene in merged)
        {
            var startFrame = scene.StartFrame;
            var endFrame = scene.EndFrame;
            var startTime = scene.StartTime;
            var endTime = scene.EndTime;

            // Look for a scene change in the first part of the scene
            for (var i = searchStart; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame.Frame > endFrame)
                {
                    break; // Frames are frame-sorted; no more matches possible
                }

                if (frame.Frame >= startFrame)
                {
                    // Record the first in-range index so the next scene can skip past earlier frames.
                    // Only set once: later frames in this scene are irrelevant for the next scene's start.
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

            finalScenes.Add(new CreditScene(startFrame, endFrame, startTime, endTime));
        }

        return finalScenes;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Analyzing {Count} episodes for credits using black frame detection")]
    private partial void LogAnalyzingEpisodes(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No valid credits found for {Episode}")]
    private partial void LogNoValidCreditsFound(string episode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found credits for {Episode} at {Start:F2}s")]
    private partial void LogFoundCredits(string episode, double start);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis cancelled by user")]
    private partial void LogAnalysisCancelled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Error analyzing {Episode} for credits")]
    private partial void LogErrorAnalyzingCredits(Exception ex, string episode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found valid credits segment: start={Start:F2}s, end={End:F2}s, duration={Duration:F2}s")]
    private partial void LogFoundValidCreditsSegment(double start, double end, double duration);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refined credit boundary from {OriginalStart:F2}s to {RefinedStart:F2}s")]
    private partial void LogRefinedBoundary(double originalStart, double refinedStart);
}
