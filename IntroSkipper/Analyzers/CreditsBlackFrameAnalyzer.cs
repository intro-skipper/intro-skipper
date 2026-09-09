// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Capy Agent
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Detects end credits from keyframe black-frame evidence.
/// </summary>
/// <remarks>
/// Uses adaptive density gating, targeted blackdetect interval recovery, and optional boundary
/// refinement to handle dark scenes and sparse keyframe samples.
/// </remarks>
/// <param name="logger">Logger for the analyzer.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="configuration">Plugin configuration, or <see langword="null"/> to use the active plugin configuration.</param>
internal sealed partial class CreditsBlackFrameAnalyzer(
    ILogger<CreditsBlackFrameAnalyzer> logger,
    IFFmpegService ffmpegService,
    PluginConfiguration? configuration = null)
{
    private readonly PluginConfiguration _config = configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<CreditsBlackFrameAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;

    /// <summary>
    /// Detects one episode's credits with the configured thresholds, without adjusting times
    /// or writing. The credits pass combines the result with the other analyzers' candidates.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>The credits segment, or <see langword="null"/> when none was found. Probe failures propagate to the caller, which marks the episode failed.</returns>
    internal Task<Segment?> DetectCreditsAsync(QueuedEpisode episode, CancellationToken cancellationToken)
        => DetectCreditsAsync(episode, _config.BlackFrameMinimumPercentage, _config.BlackFrameThreshold, _config.MinimumCreditsDuration, cancellationToken);

    /// <summary>
    /// Detects the start of credits from FFmpeg keyframe evidence.
    /// </summary>
    /// <remarks>
    /// Tries the black-frame scan first (frame-accurate for credits on black). When that finds
    /// nothing and <see cref="PluginConfiguration.DetectNonBlackCredits"/> is enabled, falls back to a
    /// low-entropy keyframe scan that recognises credits on a near-uniform low-saturation card.
    /// </remarks>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="minimumPercentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>A task that returns the time range of the detected credits.</returns>
    internal async Task<Segment?> DetectCreditsAsync(QueuedEpisode episode, int minimumPercentage, int threshold, int minimumDuration, CancellationToken cancellationToken = default)
    {
        var blackFrames = (await _ffmpegService.DetectBlackFramesAsync(episode, threshold, cancellationToken).ConfigureAwait(false)).ToList();

        var segment = blackFrames.Count > 0
            ? await DetectBlackFrameCreditsAsync(episode, blackFrames, minimumPercentage, threshold, minimumDuration, cancellationToken).ConfigureAwait(false)
            : null;

        if (segment is not null)
        {
            return segment;
        }

        if (_config.DetectNonBlackCredits)
        {
            return await DetectNonBlackCreditsAsync(episode, minimumDuration, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Detects credits from black-frame keyframe evidence, with optional blackdetect interval recovery and boundary refinement.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="blackFrames">The keyframe black-frame scan results.</param>
    /// <param name="minimumPercentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>A task that returns the detected credits segment, or <see langword="null" /> when no valid black-frame credits exist.</returns>
    private async Task<Segment?> DetectBlackFrameCreditsAsync(QueuedEpisode episode, List<BlackFrame> blackFrames, int minimumPercentage, int threshold, int minimumDuration, CancellationToken cancellationToken)
    {
        var (minimum, sceneChange) = BlackFrameThresholdHelper.NormalizeThreshold(blackFrames, minimumPercentage);
        var scenes = CreditSceneBuilder.DetectCreditScenes(blackFrames, minimum, sceneChange, minimumDuration, _config.RefineCreditsBoundary);
        var blackIntervals = Array.Empty<BlackInterval>();

        if (scenes.Count == 0)
        {
            var candidates = CreditSceneBuilder.FindRawScenes(blackFrames, minimum);
            if (candidates.Count == 0)
            {
                return null;
            }

            blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, candidates, threshold, minimum, minimumDuration, cancellationToken).ConfigureAwait(false);
            scenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, blackIntervals, minimum, minimumDuration);
            if (scenes.Count == 0)
            {
                return null;
            }
        }
        else if (scenes.Count == 1 && CreditSceneMetricsCalculator.Calculate(blackFrames, scenes[0], minimum).IsSparse(scenes[0], minimumDuration))
        {
            blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, scenes, threshold, minimum, minimumDuration, cancellationToken).ConfigureAwait(false);
            var supportedScenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, blackIntervals, minimum, minimumDuration);
            if (supportedScenes.Count > 0)
            {
                scenes = supportedScenes;
            }
        }

        foreach (var scene in RankCreditCandidates(scenes, blackIntervals))
        {
            var refinedStartTime = _config.RefineCreditsBoundary
                ? await RefineBoundaryAsync(episode, blackFrames, scene, sceneChange, threshold, minimumDuration, cancellationToken).ConfigureAwait(false)
                : scene.StartTime;

            var segment = new Segment(
                episode.EpisodeId,
                new TimeRange(refinedStartTime + episode.CreditsFingerprintStart, scene.EndTime + episode.CreditsFingerprintStart));

            if (segment.Duration >= minimumDuration)
            {
                LogFoundValidCreditsSegment(segment.Start, segment.End, segment.Duration);

                return segment;
            }
        }

        return null;
    }

    /// <summary>
    /// Recovers non-black credits (text on a near-uniform low-saturation card) from a low-entropy keyframe scan.
    /// </summary>
    /// <remarks>
    /// Only runs when the black-frame scan found no valid credits. The entropy gate is what suppresses
    /// dark non-credit scenes: those are high entropy and never match a uniform credit card.
    /// </remarks>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>A task that returns the detected credits segment, or <see langword="null" /> when no non-black credits exist.</returns>
    private async Task<Segment?> DetectNonBlackCreditsAsync(QueuedEpisode episode, int minimumDuration, CancellationToken cancellationToken)
    {
        var visuals = await _ffmpegService.DetectKeyframeVisualsAsync(episode, cancellationToken).ConfigureAwait(false);
        var range = CreditEntropyFallback.FindCreditRange(visuals, minimumDuration);
        if (range is null)
        {
            return null;
        }

        var segment = new Segment(
            episode.EpisodeId,
            new TimeRange(range.Start + episode.CreditsFingerprintStart, range.End + episode.CreditsFingerprintStart));

        LogFoundNonBlackCredits(segment.Start, segment.End, segment.Duration);

        return segment;
    }

    /// <summary>
    /// Runs targeted blackdetect scans for candidate ranges and converts failures into an empty result.
    /// </summary>
    /// <param name="episode">The episode being analyzed.</param>
    /// <param name="candidates">The candidate scenes that bound interval probes.</param>
    /// <param name="threshold">The FFmpeg blackdetect threshold.</param>
    /// <param name="minimum">The black-frame percentage threshold, passed through to blackdetect pic_th so interval confirmation uses the same definition of "black" as the keyframe pass.</param>
    /// <param name="minimumDuration">The minimum credit duration.</param>
    /// <param name="cancellationToken">The token used to cancel FFmpeg probing.</param>
    /// <returns>The detected black intervals, or an empty array when interval detection is unavailable.</returns>
    private async Task<BlackInterval[]> DetectBlackIntervalsForCandidatesOrEmptyAsync(
        QueuedEpisode episode,
        IReadOnlyList<CreditScene> candidates,
        int threshold,
        int minimum,
        int minimumDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var intervals = new List<BlackInterval>();
            var (fingerprintStart, fingerprintEnd) = episode.GetFingerprintRange(AnalysisMode.Credits);
            foreach (var range in BuildIntervalProbeRanges(candidates, minimumDuration, fingerprintStart, fingerprintEnd))
            {
                intervals.AddRange(await _ffmpegService.DetectBlackIntervalsAsync(episode, range, threshold, minimum, cancellationToken).ConfigureAwait(false));
            }

            return [.. intervals];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogBlackIntervalDetectionUnavailable(ex, episode.Name);
            return [];
        }
    }

    /// <summary>
    /// Refines a scene start time when a targeted blackframe probe of the keyframe gap before the
    /// scene finds an earlier black transition. Probes only when the gap could change whether the
    /// scene reaches the minimum duration.
    /// </summary>
    /// <returns>The refined start time, or the original scene start when no valid refinement exists.</returns>
    private async Task<double> RefineBoundaryAsync(
        QueuedEpisode episode,
        List<BlackFrame> frames,
        CreditScene scene,
        int sceneChange,
        int threshold,
        int minimumDuration,
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
        var probeRange = new TimeRange(
            lastKeyframeTime + episode.CreditsFingerprintStart,
            firstBlackTime + episode.CreditsFingerprintStart);

        var probeFrames = await _ffmpegService
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

        LogRefinedBoundary(scene.StartTime, refinedTime.Value);
        return refinedTime.Value;
    }

    /// <summary>
    /// Builds bounded blackdetect probe ranges for candidate scenes.
    /// </summary>
    /// <param name="candidates">The candidate scenes that bound interval probes.</param>
    /// <param name="minimumDuration">The minimum credit duration, also used as probe padding on each side.</param>
    /// <param name="fingerprintStart">The absolute start of the credits fingerprint window.</param>
    /// <param name="fingerprintEnd">The absolute end of the credits fingerprint window.</param>
    /// <returns>The merged probe ranges clamped to the fingerprint window.</returns>
    internal static List<TimeRange> BuildIntervalProbeRanges(
        IReadOnlyList<CreditScene> candidates,
        int minimumDuration,
        double fingerprintStart,
        double fingerprintEnd)
    {
        var ranges = candidates
            .Select(candidate => new TimeRange(
                Math.Max(fingerprintStart, fingerprintStart + candidate.StartTime - minimumDuration),
                Math.Min(fingerprintEnd, fingerprintStart + candidate.EndTime + minimumDuration)))
            .Where(range => range.Duration > 0)
            .OrderBy(range => range.Start)
            .ToList();

        if (ranges.Count <= 1)
        {
            return ranges;
        }

        var merged = new List<TimeRange>(ranges.Count);
        var current = ranges[0];
        for (var i = 1; i < ranges.Count; i++)
        {
            var next = ranges[i];
            if (next.Start <= current.End)
            {
                current.End = Math.Max(current.End, next.End);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    /// <summary>
    /// Ranks credit candidates, preferring scenes with interval support and then later scenes.
    /// </summary>
    /// <param name="scenes">The detected candidate scenes.</param>
    /// <param name="intervals">The blackdetect intervals available for scoring.</param>
    /// <returns>The ranked candidate scenes.</returns>
    internal static List<CreditScene> RankCreditCandidates(
        IReadOnlyList<CreditScene> scenes,
        IReadOnlyList<BlackInterval> intervals)
    {
        return [.. scenes
            .Select((scene, index) => new
            {
                Scene = scene,
                Index = index,
                HasIntervalSupport = HasIntervalSupport(scene, intervals),
            })
            .OrderByDescending(candidate => candidate.HasIntervalSupport)
            .ThenByDescending(candidate => candidate.Index)
            .Select(candidate => candidate.Scene)];
    }

    /// <summary>
    /// Determines whether a candidate scene overlaps a confirmed black interval.
    /// </summary>
    private static bool HasIntervalSupport(CreditScene scene, IReadOnlyList<BlackInterval> intervals)
    {
        foreach (var interval in intervals)
        {
            var overlapStart = Math.Max(scene.StartTime, interval.Start);
            var overlapEnd = Math.Min(scene.EndTime, interval.End);
            if (overlapEnd - overlapStart >= CreditDetectionPolicy.MinimumIntervalOverlapSeconds)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found valid credits segment: start={Start:F2}s, end={End:F2}s, duration={Duration:F2}s")]
    private partial void LogFoundValidCreditsSegment(double start, double end, double duration);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found non-black credits segment: start={Start:F2}s, end={End:F2}s, duration={Duration:F2}s")]
    private partial void LogFoundNonBlackCredits(double start, double end, double duration);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refined credit boundary from {OriginalStart:F2}s to {RefinedStart:F2}s")]
    private partial void LogRefinedBoundary(double originalStart, double refinedStart);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Black interval detection unavailable for {Episode}")]
    private partial void LogBlackIntervalDetectionUnavailable(Exception ex, string episode);
}
