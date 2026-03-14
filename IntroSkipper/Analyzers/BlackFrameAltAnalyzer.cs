// SPDX-FileCopyrightText: 2025-2026 rlauuzo
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
/// Uses an adaptive binary search algorithm to efficiently locate the start of credits.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BlackFrameAltAnalyzer"/> class.
/// </remarks>
/// <param name="logger">Logger for the analyzer.</param>
public sealed partial class BlackFrameAltAnalyzer(ILogger<BlackFrameAltAnalyzer> logger) : IMediaFileAnalyzer
{
    private const int MaximumTimeSkip = 15;
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
            .Where(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed)
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
                await plugin.UpdateTimestampAsync(credit, mode, cancellationToken).ConfigureAwait(false);
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

        var scenes = DetectCreditScenes(blackFrames, minimumPercentage);
        if (scenes.Count == 0)
        {
            return null;
        }

        // Start from the last scene and work backwards to find the first valid credits segment
        for (var i = scenes.Count - 1; i >= 0; i--)
        {
            var scene = scenes[i];
            var segment = new Segment(episode.EpisodeId, new TimeRange(scene.StartTime + episode.CreditsFingerprintStart, scene.EndTime + episode.CreditsFingerprintStart));

            if (segment.Duration >= minimumDuration)
            {
                LogFoundValidCreditsSegment(segment.Start, segment.End, segment.Duration);

                return segment;
            }
        }

        return null;
    }

    private static List<CreditScene> DetectCreditScenes(List<BlackFrame> frames, int minimumPercentage)
    {
        var scenes = new List<CreditScene>();
        BlackFrame? sceneStart = null;
        BlackFrame? lastBlack = null;

        // Normalize the threshold based on consistent dark elements, capping floor at 30%
        // Adapts the detection sensitivity to the video's natural black levels
        var orderedFrames = frames.OrderBy(f => f.Percentage).ToList();
        var percentileIndex = (int)(frames.Count * 0.01); // 1st percentile
        var floor = Math.Min(orderedFrames[percentileIndex].Percentage, 30);
        var minimum = (minimumPercentage * (100 - floor) / 100) + floor;
        var sceneChange = (95 * (100 - floor) / 100) + floor;

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

        // Merge scenes that are close together
        if (scenes.Count <= 1)
        {
            return scenes;
        }

        var merged = new List<CreditScene>(scenes.Count);
        var current = scenes[0];

        for (var i = 1; i < scenes.Count; i++)
        {
            var scene = scenes[i];
            if (scene.StartTime - current.EndTime <= MaximumTimeSkip)
            {
                current = new CreditScene(current.StartFrame, scene.EndFrame, current.StartTime, scene.EndTime);
            }
            else
            {
                merged.Add(current);
                current = scene;
            }
        }

        merged.Add(current);

        // Find the transition frame for each merged scene
        var finalScenes = new List<CreditScene>(merged.Count);
        foreach (var scene in merged)
        {
            var startFrame = scene.StartFrame;
            var endFrame = scene.EndFrame;
            var startTime = scene.StartTime;
            var endTime = scene.EndTime;

            // Look for a scene change in the first part of the scene
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame.Frame >= startFrame && frame.Frame <= endFrame && frame.Percentage >= sceneChange)
                {
                    startFrame = frame.Frame;
                    startTime = frame.Time;
                    break;
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
}
