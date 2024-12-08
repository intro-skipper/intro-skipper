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
/// Bisects the end of the video file to perform an efficient search.
/// </summary>
public class BlackFrameAnalyzer(ILogger<BlackFrameAnalyzer> logger) : IMediaFileAnalyzer
{
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly TimeSpan _maximumError = new(0, 0, 4);
    private readonly ILogger<BlackFrameAnalyzer> _logger = logger;

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != AnalysisMode.Credits)
        {
            throw new NotImplementedException("mode must equal Credits");
        }

        var episodesWithoutIntros = analysisQueue.Where(e => !e.IsAnalyzed).ToList();

        var searchStart = 0.0;

        foreach (var episode in episodesWithoutIntros.TakeWhile(episode => !cancellationToken.IsCancellationRequested))
        {
            if (!AnalyzeChapters(episode, out var credit))
            {
                if (searchStart < _config.MinimumCreditsDuration)
                {
                    searchStart = FindSearchStart(episode);
                }

                credit = AnalyzeMediaFile(
                    episode,
                    searchStart,
                    _config.BlackFrameMinimumPercentage);
            }

            if (credit is null || !credit.Valid)
            {
                continue;
            }

            episode.IsAnalyzed = episode.IsMovie;
            await Plugin.Instance!.UpdateTimestampAsync(credit, mode).ConfigureAwait(false);
            searchStart = episode.Duration - credit.Start + _config.MinimumCreditsDuration;
        }

        return analysisQueue;
    }

    /// <summary>
    /// Analyzes an individual media file. Only public because of unit tests.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="searchStart">Search Start Piont.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <returns>Credits timestamp.</returns>
    public Segment? AnalyzeMediaFile(QueuedEpisode episode, double searchStart, int minimum)
    {
        // Start by analyzing the last N minutes of the file.
        var searchDistance = 2 * _config.MinimumCreditsDuration;
        var upperLimit = Math.Min(searchStart, episode.Duration - episode.CreditsFingerprintStart);
        var lowerLimit = Math.Max(searchStart - searchDistance, _config.MinimumCreditsDuration);
        var start = TimeSpan.FromSeconds(upperLimit);
        var end = TimeSpan.FromSeconds(lowerLimit);
        var firstFrameTime = 0.0;

        // Continue bisecting the end of the file until the range that contains the first black
        // frame is smaller than the maximum permitted error.
        while (start - end > _maximumError)
        {
            // Analyze the middle two seconds from the current bisected range
            var midpoint = (start + end) / 2;
            var scanTime = episode.Duration - midpoint.TotalSeconds;
            var tr = new TimeRange(scanTime, scanTime + 2);

            _logger.LogTrace(
                "{Episode}, dur {Duration}, bisect [{BStart}, {BEnd}], time [{Start}, {End}]",
                episode.Name,
                episode.Duration,
                start,
                end,
                tr.Start,
                tr.End);

            var frames = FFmpegWrapper.DetectBlackFrames(episode, tr, minimum);
            _logger.LogTrace(
                "{Episode} at {Start} has {Count} black frames",
                episode.Name,
                tr.Start,
                frames.Length);

            if (frames.Length == 0)
            {
                // Since no black frames were found, slide the range closer to the end
                start = midpoint - TimeSpan.FromSeconds(2);

                if (midpoint - TimeSpan.FromSeconds(lowerLimit) < _maximumError)
                {
                    lowerLimit = Math.Max(lowerLimit - (0.5 * searchDistance), _config.MinimumCreditsDuration);

                    // Reset end for a new search with the increased duration
                    end = TimeSpan.FromSeconds(lowerLimit);
                }
            }
            else
            {
                // Some black frames were found, slide the range closer to the start
                end = midpoint;
                firstFrameTime = frames[0].Time + scanTime;

                if (TimeSpan.FromSeconds(upperLimit) - midpoint < _maximumError)
                {
                    upperLimit = Math.Min(upperLimit + (0.5 * searchDistance), episode.Duration - episode.CreditsFingerprintStart);

                    // Reset start for a new search with the increased duration
                    start = TimeSpan.FromSeconds(upperLimit);
                }
            }
        }

        if (firstFrameTime > 0)
        {
            return new Segment(episode.EpisodeId, new TimeRange(firstFrameTime, episode.Duration));
        }

        return null;
    }

    private bool AnalyzeChapters(QueuedEpisode episode, out Segment? segment)
    {
        // Get last chapter that falls within the valid credits duration range
        var suitableChapters = Plugin.Instance!.GetChapters(episode.EpisodeId)
            .Select(c => TimeSpan.FromTicks(c.StartPositionTicks).TotalSeconds)
            .Where(s => s >= episode.CreditsFingerprintStart &&
                s <= episode.Duration - _config.MinimumCreditsDuration)
            .OrderByDescending(s => s).ToList();

        // If suitable chapters found, use them to find the search start point
        foreach (var chapterStart in suitableChapters)
        {
            // Check for black frames at chapter start
            var startRange = new TimeRange(chapterStart, chapterStart + 1);
            var hasBlackFramesAtStart = FFmpegWrapper.DetectBlackFrames(
                episode,
                startRange,
                _config.BlackFrameMinimumPercentage).Length > 0;

            if (!hasBlackFramesAtStart)
            {
                break;
            }

            // Verify no black frames before chapter start
            var beforeRange = new TimeRange(chapterStart - 5, chapterStart - 4);
            var hasBlackFramesBefore = FFmpegWrapper.DetectBlackFrames(
                episode,
                beforeRange,
                _config.BlackFrameMinimumPercentage).Length > 0;

            if (!hasBlackFramesBefore)
            {
                segment = new Segment(episode.EpisodeId, new TimeRange(chapterStart, episode.Duration));
                return true;
            }
        }

        segment = null;
        return false;
    }

    private double FindSearchStart(QueuedEpisode episode)
    {
        var searchStart = 3 * _config.MinimumCreditsDuration;
        var scanTime = episode.Duration - searchStart;
        var tr = new TimeRange(scanTime - 0.5, scanTime); // Short search range since accuracy isn't important here.

        // Keep increasing search start time while black frames are found, to avoid false positives
        while (FFmpegWrapper.DetectBlackFrames(episode, tr, _config.BlackFrameMinimumPercentage).Length > 0)
        {
            // Increase by 2x minimum credits duration each iteration
            searchStart += 2 * _config.MinimumCreditsDuration;
            scanTime = episode.Duration - searchStart;
            tr = new TimeRange(scanTime - 0.5, scanTime);

            // Don't search past the required credits duration from the end
            if (searchStart > episode.Duration - episode.CreditsFingerprintStart)
            {
                searchStart = episode.Duration - episode.CreditsFingerprintStart;
                break;
            }
        }

        return searchStart;
    }
}
