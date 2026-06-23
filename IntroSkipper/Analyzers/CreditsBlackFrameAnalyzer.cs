// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
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
public sealed partial class CreditsBlackFrameAnalyzer(ILogger<CreditsBlackFrameAnalyzer> logger, IFFmpegService ffmpegService) : IMediaFileAnalyzer
{
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<CreditsBlackFrameAnalyzer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IFFmpegService _ffmpegService = ffmpegService;

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != AnalysisMode.Credits)
        {
            throw new NotImplementedException($"{nameof(CreditsBlackFrameAnalyzer)} only supports {nameof(AnalysisMode.Credits)} mode");
        }

        var unanalyzedEpisodes = analysisQueue
            .Where(e => e.NeedsAnalysis(mode))
            .ToList();

        if (unanalyzedEpisodes.Count == 0)
        {
            return analysisQueue;
        }

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config, mode, _ffmpegService);

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
                var credit = await DetectCreditsAsync(episode, minimumPercentage, threshold, minimumDuration, cancellationToken).ConfigureAwait(false);

                if (credit is null || !credit.Valid)
                {
                    LogNoValidCreditsFound(episode.Name);
                    continue;
                }

                credit = await timeAdjustmentHelper.AdjustIntroTimesAsync(episode, credit, cancellationToken: cancellationToken).ConfigureAwait(false);
                LogFoundCredits(episode.Name, credit.Start);

                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                await plugin.UpdateTimestampAsync(credit, mode, configHash: episode.AnalysisConfigHash, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogAnalysisCancelled();
                throw;
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
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>A task that returns the time range of the detected credits.</returns>
    public async Task<Segment?> DetectCreditsAsync(QueuedEpisode episode, int minimumPercentage, int threshold, int minimumDuration, CancellationToken cancellationToken = default)
    {
        var blackFrames = (await _ffmpegService.DetectBlackFramesAsync(episode, threshold, cancellationToken).ConfigureAwait(false)).ToList();

        if (blackFrames.Count == 0)
        {
            return null;
        }

        var (minimum, sceneChange) = NormalizeThreshold(blackFrames, minimumPercentage);
        var scenes = CreditSceneBuilder.DetectCreditScenes(blackFrames, minimum, sceneChange, minimumDuration, _config.RefineCreditsBoundary);
        var blackIntervals = Array.Empty<BlackInterval>();

        if (scenes.Count == 0)
        {
            var candidates = CreditSceneBuilder.DetectCreditSceneCandidates(blackFrames, minimum);
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

        var ranked = RankCreditCandidates(scenes, blackIntervals);
        var boundaryRefiner = _config.RefineCreditsBoundary ? new CreditsBoundaryRefiner(_ffmpegService) : null;

        foreach (var scene in ranked)
        {
            var refinedStartTime = scene.StartTime;
            if (boundaryRefiner is not null)
            {
                refinedStartTime = await boundaryRefiner
                    .RefineAsync(episode, blackFrames, scene, sceneChange, threshold, minimumDuration, LogRefinedBoundary, cancellationToken)
                    .ConfigureAwait(false);
            }

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
    /// Normalizes black-frame thresholds against the darkest frames in the credits scan.
    /// </summary>
    /// <param name="frames">The keyframe black-frame scan results.</param>
    /// <param name="minimumPercentage">The configured minimum black percentage.</param>
    /// <returns>The normalized black-frame and scene-change thresholds.</returns>
    internal static (int Minimum, int SceneChange) NormalizeThreshold(List<BlackFrame> frames, int minimumPercentage)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count, nameof(frames));

        var orderedFrames = frames.OrderBy(f => f.Percentage).ToList();
        var percentileIndex = (int)(frames.Count * 0.01);
        var floor = Math.Min(orderedFrames[percentileIndex].Percentage, 30);
        var minimum = (minimumPercentage * (100 - floor) / 100) + floor;
        var sceneChange = (95 * (100 - floor) / 100) + floor;
        return (minimum, sceneChange);
    }

    /// <summary>
    /// Builds bounded blackdetect probe ranges for candidate scenes.
    /// </summary>
    /// <param name="candidates">The candidate scenes that bound interval probes.</param>
    /// <param name="minimumDuration">The minimum credit duration used as probe padding.</param>
    /// <param name="fingerprintStart">The absolute start of the credits fingerprint window.</param>
    /// <param name="fingerprintEnd">The absolute end of the credits fingerprint window.</param>
    /// <returns>The merged probe ranges clamped to the fingerprint window.</returns>
    internal static List<TimeRange> BuildIntervalProbeRanges(
        IReadOnlyList<CreditScene> candidates,
        int minimumDuration,
        double fingerprintStart,
        double fingerprintEnd)
    {
        var padding = CreditDetectionPolicy.IntervalProbePadding(minimumDuration);
        var ranges = candidates
            .Select(candidate => new TimeRange(
                Math.Max(fingerprintStart, fingerprintStart + candidate.StartTime - padding),
                Math.Min(fingerprintEnd, fingerprintStart + candidate.EndTime + padding)))
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
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(intervals);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Black interval detection unavailable for {Episode}")]
    private partial void LogBlackIntervalDetectionUnavailable(Exception ex, string episode);
}
