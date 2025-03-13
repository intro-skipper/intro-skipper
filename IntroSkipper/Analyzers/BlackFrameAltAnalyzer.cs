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

        foreach (var episode in unanalyzedEpisodes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var credit = DetectCredits(
                        episode,
                        _config.BlackFrameMinimumPercentage);

                if (credit is null || !credit.Valid)
                {
                    _logger.LogDebug("No valid credits found for {Episode}", episode.Name);
                    continue;
                }

                _logger.LogDebug("Found credits for {Episode} at {Start:F2}s", episode.Name, credit.Start);

                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                await Plugin.Instance!.UpdateTimestampAsync(credit, mode).ConfigureAwait(false);
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
        _logger.LogDebug("Detected {Count} black frames for {Episode}", blackFrames.Length, episode.Name);
        if (blackFrames.Length == 0)
        {
            return null;
        }

        var scenes = DetectCreditScenes(blackFrames, minimum);
        if (scenes.Length == 0)
        {
            return null;
        }

        var lastScene = scenes[^1];
        var start = lastScene.StartTime + episode.CreditsFingerprintStart;
        var end = lastScene.EndTime == blackFrames[^1].Time ? episode.Duration : lastScene.EndTime + episode.CreditsFingerprintStart;

        return new Segment(episode.EpisodeId, new TimeRange(start, end));
    }

    private static CreditScene[] DetectCreditScenes(BlackFrame[] blackFrames, int minimum = 85)
    {
        var scenes = new List<CreditScene>();
        BlackFrame? sceneStart = null;
        BlackFrame? lastBlack = null;

        foreach (var frame in blackFrames)
        {
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

            // End scene if gap is too large
            else if (sceneStart is not null && lastBlack is not null && frame.Frame - lastBlack.Frame > 5)
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
        if (scenes.Count == 0)
        {
            return [];
        }

        var merged = new List<CreditScene>();
        var current = scenes[0];

        foreach (var scene in scenes.Skip(1))
        {
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

        return [.. merged];
    }
}
