// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

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
public sealed class BlackFrameAltAnalyzer(ILogger<BlackFrameAltAnalyzer> logger) : IMediaFileAnalyzer
{
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

        _logger.LogDebug("Analyzing {Count} episodes for credits using black frame detection", unanalyzedEpisodes.Count);

        var minimumPercentage = _config.BlackFrameMinimumPercentage;
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");

        foreach (var episode in unanalyzedEpisodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var credit = DetectCredits(episode, minimumPercentage);

                if (credit is null || !credit.Valid)
                {
                    _logger.LogDebug("No valid credits found for {Episode}", episode.Name);
                    continue;
                }

                _logger.LogDebug("Found credits for {Episode} at {Start:F2}s", episode.Name, credit.Start);

                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                await plugin.UpdateTimestampAsync(credit, mode).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Analysis cancelled by user");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing {Episode} for credits", episode.Name);
            }
        }

        return analysisQueue;
    }

    /// <summary>
    /// Detects the start of blackframe credits from FFmpeg blackframe filter output.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="minimum">Minimum percentage of black pixels to consider a frame a black frame.</param>
    /// <returns>Time range of the detected credits.</returns>
    public Segment? DetectCredits(QueuedEpisode episode, int minimum)
    {
        var blackFrames = FFmpegWrapper.DetectBlackFrames(episode);

        if (blackFrames.Length == 0)
        {
            return null;
        }

        var scenes = DetectCreditScenes(blackFrames, minimum);
        if (scenes.Count == 0)
        {
            return null;
        }

        var lastFrameTime = blackFrames[^1].Time;
        var minimumDuration = _config.MinimumCreditsDuration;

        // Start from the last scene and work backwards to find the first valid credits segment
        for (var i = scenes.Count - 1; i >= 0; i--)
        {
            var scene = scenes[i];
            var start = scene.StartTime + episode.CreditsFingerprintStart;
            var end = scene.EndTime == lastFrameTime ? episode.Duration : scene.EndTime + episode.CreditsFingerprintStart;
            var duration = end - start;

            if (duration >= minimumDuration)
            {
                _logger.LogTrace(
                    "Found valid credits segment: start={Start:F2}s, end={End:F2}s, duration={Duration:F2}s",
                    start,
                    end,
                    duration);

                return new Segment(episode.EpisodeId, new TimeRange(start, end));
            }
        }

        return null;
    }

    private static List<CreditScene> DetectCreditScenes(BlackFrame[] blackFrames, int minimum = 85)
    {
        var scenes = new List<CreditScene>();
        BlackFrame? sceneStart = null;
        BlackFrame? lastBlack = null;

        Span<BlackFrame> frames = blackFrames;

        for (var i = 0; i < frames.Length; i++)
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
                    (i == frames.Length - 1 || frame.Frame - lastBlack.Frame > 5))
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
            if (scene.StartFrame - current.EndFrame <= 10)
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

            // Look for a transition frame (95% black) in the first part of the scene
            for (var i = 0; i < frames.Length; i++)
            {
                var frame = frames[i];
                if (frame.Frame >= startFrame && frame.Frame <= endFrame && frame.Percentage >= 95)
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
}
