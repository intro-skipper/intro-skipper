// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Detects credits from the keyframe scan: a black roll from black-frame evidence and card credits
/// from keyframe visuals, each as its own candidate for the credits pass.
/// </summary>
/// <remarks>
/// One decode reports both. Black-frame evidence is frame-accurate for credits on black and goes
/// through density gating, blackdetect interval recovery for sparse candidates and optional boundary
/// refinement. The card run is found against the black scenes those rules accepted: once they accept
/// any scene, a black keyframe is a card only inside one (see <see cref="CardRunFinder"/>).
/// </remarks>
/// <param name="logger">Logger for the analyzer.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="configuration">Plugin configuration, or <see langword="null"/> to use the active plugin configuration.</param>
internal sealed partial class KeyframeAnalyzer(
    ILogger<KeyframeAnalyzer> logger,
    IFFmpegService ffmpegService,
    PluginConfiguration? configuration = null)
{
    private readonly PluginConfiguration _config = configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<KeyframeAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;

    /// <summary>
    /// Detects one episode's credits with the configured thresholds, without adjusting times or
    /// writing. The credits pass combines the result with the other analyzers' candidates.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>Zero, one or two candidates: the black-frame candidate under <see cref="SegmentSource.BlackFrame"/> and the card run under <see cref="SegmentSource.KeyframeVisuals"/>. Probe failures propagate to the caller, which marks the episode failed.</returns>
    internal Task<IReadOnlyList<AttributedSegment>> DetectCreditsAsync(QueuedEpisode episode, CancellationToken cancellationToken)
        => DetectCreditsAsync(episode, _config.BlackFrameMinimumPercentage, _config.BlackFrameThreshold, _config.MinimumCreditsDuration, _config.DetectNonBlackCredits, cancellationToken);

    /// <summary>
    /// Detects the credits from FFmpeg keyframe evidence with explicit thresholds.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="minimumPercentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <param name="detectCardCredits">Whether to look for a card run in the keyframe visuals as well.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>Zero, one or two candidates: the black-frame candidate under <see cref="SegmentSource.BlackFrame"/> and the card run under <see cref="SegmentSource.KeyframeVisuals"/>.</returns>
    internal async Task<IReadOnlyList<AttributedSegment>> DetectCreditsAsync(QueuedEpisode episode, int minimumPercentage, int threshold, int minimumDuration, bool detectCardCredits, CancellationToken cancellationToken = default)
    {
        var blackFrames = (await _ffmpegService.DetectBlackFramesAsync(episode, threshold, cancellationToken).ConfigureAwait(false)).ToList();
        var (blackMinimum, sceneChange) = blackFrames.Count > 0
            ? BlackFrameThresholdHelper.NormalizeThreshold(blackFrames, minimumPercentage)
            : (minimumPercentage, minimumPercentage);
        var (credits, scenes) = blackFrames.Count > 0
            ? await DetectBlackFrameCreditsAsync(episode, blackFrames, blackMinimum, sceneChange, threshold, minimumDuration, cancellationToken).ConfigureAwait(false)
            : (null, []);

        var candidates = new List<AttributedSegment>(2);
        if (credits is not null)
        {
            candidates.Add(new AttributedSegment(credits, SegmentSource.BlackFrame));
        }

        if (detectCardCredits)
        {
            // The keyframe scan that produced the black-frame row wrote the visuals row too, so this
            // is normally a cache read.
            var visuals = await _ffmpegService.DetectKeyframeVisualsAsync(episode, cancellationToken).ConfigureAwait(false);
            var range = CardRunFinder.FindCreditRange(visuals, blackFrames, blackMinimum, minimumDuration, scenes);
            if (range is not null)
            {
                candidates.Add(new AttributedSegment(
                    new Segment(episode.EpisodeId, new TimeRange(range.Start + episode.CreditsFingerprintStart, range.End + episode.CreditsFingerprintStart)),
                    SegmentSource.KeyframeVisuals));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Detects credits from black-frame keyframe evidence, with optional blackdetect interval recovery and boundary refinement.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="blackFrames">The keyframe black-frame scan results.</param>
    /// <param name="minimum">The black percentage at or above which a keyframe is black, normalized against the scan.</param>
    /// <param name="sceneChange">The black percentage that marks the transition into credits, normalized against the scan.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="minimumDuration">Minimum duration of the credits.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>The credits candidate in file time and every black scene the rules accepted, relative to the credits fingerprint start, the picked one with its refined start; <see langword="null"/> and an empty list when no accepted scene met the minimum duration.</returns>
    private async Task<(Segment? Credits, List<TimeRange> Scenes)> DetectBlackFrameCreditsAsync(QueuedEpisode episode, List<BlackFrame> blackFrames, int minimum, int sceneChange, int threshold, int minimumDuration, CancellationToken cancellationToken)
    {
        var scenes = CreditSceneBuilder.DetectCreditScenes(blackFrames, minimum, sceneChange, minimumDuration, _config.RefineCreditsBoundary);
        var blackIntervals = Array.Empty<BlackInterval>();

        if (scenes.Count == 0)
        {
            var candidates = CreditSceneBuilder.FindRawScenes(blackFrames, minimum);
            if (candidates.Count == 0)
            {
                return (null, []);
            }

            blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, candidates, threshold, minimum, minimumDuration, cancellationToken).ConfigureAwait(false);
            scenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, blackIntervals, minimum, minimumDuration);
            if (scenes.Count == 0)
            {
                return (null, []);
            }
        }
        else if (scenes.Any(scene => CreditSceneMetricsCalculator.Calculate(blackFrames, scene, minimum).IsSparse(scene, minimumDuration)))
        {
            // Probe sparse scenes with blackdetect: this filters fades and scene transitions
            // without rejecting a genuine roll when the optional probe has no result.
            blackIntervals = await DetectBlackIntervalsForCandidatesOrEmptyAsync(episode, scenes, threshold, minimum, minimumDuration, cancellationToken).ConfigureAwait(false);
            var supportedScenes = CreditSceneBuilder.DetectIntervalSupportedCreditScenes(blackFrames, blackIntervals, minimum, minimumDuration);
            if (blackIntervals.Length > 0)
            {
                if (supportedScenes.Count > 0)
                {
                    scenes = supportedScenes;
                }
                else if (scenes.Count > 1)
                {
                    return (null, []);
                }
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

                // The picked scene carries its refined start, so the transition the boundary probe
                // confirmed counts as part of the roll for the card run.
                List<TimeRange> accepted = [.. scenes.Select(accepted => new TimeRange(accepted == scene ? refinedStartTime : accepted.StartTime, accepted.EndTime))];
                return (segment, accepted);
            }
        }

        return (null, []);
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

    [LoggerMessage(Level = LogLevel.Trace, Message = "Refined credit boundary from {OriginalStart:F2}s to {RefinedStart:F2}s")]
    private partial void LogRefinedBoundary(double originalStart, double refinedStart);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Black interval detection unavailable for {Episode}")]
    private partial void LogBlackIntervalDetectionUnavailable(Exception ex, string episode);
}
