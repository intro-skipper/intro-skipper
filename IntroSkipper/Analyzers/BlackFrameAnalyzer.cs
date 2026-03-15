// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2025 AbandonedCart
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
/// Initializes a new instance of the <see cref="BlackFrameAnalyzer"/> class.
/// </remarks>
/// <param name="logger">Logger for the analyzer.</param>
public sealed partial class BlackFrameAnalyzer(ILogger<BlackFrameAnalyzer> logger) : IMediaFileAnalyzer
{
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly TimeSpan _maximumError = TimeSpan.FromSeconds(4);
    private readonly ILogger<BlackFrameAnalyzer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != AnalysisMode.Credits)
        {
            throw new NotImplementedException($"{nameof(BlackFrameAnalyzer)} only supports {nameof(AnalysisMode.Credits)} mode");
        }

        var unanalyzedEpisodes = analysisQueue
            .Where(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed)
            .ToList();

        if (unanalyzedEpisodes.Count == 0)
        {
            return analysisQueue;
        }

        LogAnalyzingEpisodes(_logger, unanalyzedEpisodes.Count);

        double searchStart = 0.0;

        var percentage = _config.BlackFrameMinimumPercentage;
        var threshold = _config.BlackFrameThreshold;

        foreach (var episode in unanalyzedEpisodes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                // First try to use chapter markers if available
                if (!_config.UseChapterMarkersBlackFrame || !TryAnalyzeChapters(episode, percentage, threshold, out var credit))
                {
                    // If no suitable chapters found, use black frame detection
                    if (searchStart < _config.MinimumCreditsDuration)
                    {
                        searchStart = FindSearchStart(episode, percentage, threshold);
                    }

                    credit = AnalyzeMediaFile(
                        episode,
                        searchStart,
                        percentage,
                        threshold);
                }

                if (credit is null || !credit.Valid)
                {
                    LogNoValidCreditsFound(_logger, episode.Name);
                    continue;
                }

                LogFoundCredits(_logger, episode.Name, credit.Start);

                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                await Plugin.Instance!.UpdateTimestampAsync(credit, mode, cancellationToken).ConfigureAwait(false);

                // Update search start for next episode based on this result
                searchStart = episode.Duration - credit.Start + _config.MinimumCreditsDuration;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogErrorAnalyzingCredits(_logger, ex, episode.Name);
            }
        }

        return analysisQueue;
    }

    /// <summary>
    /// Analyzes an individual media file to find the start of credits.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="initialStart">Initial search position from the end of the file.</param>
    /// <param name="minimumBlackPercentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <returns>Credits segment if found; otherwise null.</returns>
    public Segment? AnalyzeMediaFile(QueuedEpisode episode, double initialStart, int minimumBlackPercentage, int threshold)
    {
        ArgumentNullException.ThrowIfNull(episode);

        // Calculate search boundaries
        var searchDistance = 2 * _config.MinimumCreditsDuration;
        var upperLimit = Math.Min(initialStart, episode.Duration - episode.CreditsFingerprintStart);
        var lowerLimit = Math.Max(initialStart - searchDistance, _config.MinimumCreditsDuration);

        // Convert to TimeSpan for more accurate comparisons
        var searchStart = TimeSpan.FromSeconds(upperLimit);
        var searchEnd = TimeSpan.FromSeconds(lowerLimit);

        double? firstBlackFrameTime = null;

        try
        {
            // Continue binary search until the precision threshold is reached
            while (searchStart - searchEnd > _maximumError)
            {
                // Calculate midpoint and scan window
                var midpoint = (searchStart + searchEnd) / 2;
                var scanTime = episode.Duration - midpoint.TotalSeconds;
                var timeRange = new TimeRange(scanTime, scanTime + 2);

                // Detect black frames in the current time range
                var blackFrames = FFmpegWrapper.DetectBlackFrames(episode, timeRange, minimumBlackPercentage, threshold);

                LogBlackFramesDetected(_logger, episode.Name, timeRange.Start, blackFrames.Length);

                if (blackFrames.Length == 0)
                {
                    // No black frames found, move search range toward the end
                    searchStart = midpoint - TimeSpan.FromSeconds(2);

                    // If we're close to the lower limit, expand search range
                    if (midpoint.TotalSeconds - lowerLimit < _maximumError.TotalSeconds)
                    {
                        lowerLimit = Math.Max(lowerLimit - (0.5 * searchDistance), _config.MinimumCreditsDuration);
                        searchEnd = TimeSpan.FromSeconds(lowerLimit);

                        LogExpandedSearchLowerLimit(_logger, lowerLimit);
                    }
                }
                else
                {
                    // Black frames found, move search range toward the beginning
                    searchEnd = midpoint;
                    firstBlackFrameTime = blackFrames[0].Time + scanTime;

                    // If we're close to the upper limit, expand search range
                    if (upperLimit - midpoint.TotalSeconds < _maximumError.TotalSeconds)
                    {
                        upperLimit = Math.Min(
                            upperLimit + (0.5 * searchDistance),
                            episode.Duration - episode.CreditsFingerprintStart);
                        searchStart = TimeSpan.FromSeconds(upperLimit);

                        LogExpandedSearchUpperLimit(_logger, upperLimit);
                    }
                }
            }

            // Return a segment if we found black frames
            if (firstBlackFrameTime.HasValue && firstBlackFrameTime.Value > 0)
            {
                return new Segment(
                    episode.EpisodeId,
                    new TimeRange(firstBlackFrameTime.Value, episode.Duration));
            }

            return null;
        }
        catch (Exception ex)
        {
            LogErrorDuringAnalysis(_logger, ex, episode.Name);
            return null;
        }
    }

    /// <summary>
    /// Attempts to find credits by analyzing chapter markers.
    /// </summary>
    /// <param name="episode">Episode to analyze.</param>
    /// <param name="percentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="segment">Output segment if credits are found.</param>
    /// <returns>True if credits were found using chapters; otherwise false.</returns>
    private bool TryAnalyzeChapters(QueuedEpisode episode, int percentage, int threshold, out Segment? segment)
    {
        ArgumentNullException.ThrowIfNull(episode);

        // Get chapters that fall within the valid credits duration range
        var suitableChapters = Plugin.Instance!.GetChapters(episode.EpisodeId)
            .Select(c => TimeSpan.FromTicks(c.StartPositionTicks).TotalSeconds)
            .Where(s => s >= episode.CreditsFingerprintStart &&
                        s <= episode.Duration - _config.MinimumCreditsDuration)
            .OrderByDescending(s => s)
            .ToList();

        if (suitableChapters.Count == 0)
        {
            LogNoSuitableChaptersFound(_logger, episode.Name);
            segment = null;
            return false;
        }

        // Check each chapter to see if it marks the start of credits
        foreach (var chapterStart in suitableChapters)
        {
            // Check for black frames at chapter start
            var startRange = new TimeRange(chapterStart, chapterStart + 1);
            var hasBlackFramesAtStart = FFmpegWrapper.DetectBlackFrames(
                episode,
                startRange,
                percentage,
                threshold).Length > 0;

            if (!hasBlackFramesAtStart)
            {
                LogChapterNoBlackFramesAtStart(_logger, chapterStart);
                break;
            }

            // Verify no black frames before chapter start (to confirm this is the actual start)
            var beforeRange = new TimeRange(chapterStart - 5, chapterStart - 4);
            var hasBlackFramesBefore = FFmpegWrapper.DetectBlackFrames(
                episode,
                beforeRange,
                percentage,
                threshold).Length > 0;

            if (!hasBlackFramesBefore)
            {
                LogFoundCreditsWithChapterMarker(_logger, chapterStart);
                segment = new Segment(episode.EpisodeId, new TimeRange(chapterStart, episode.Duration));
                return true;
            }
        }

        segment = null;
        return false;
    }

    /// <summary>
    /// Finds an optimal starting point for the credits search to avoid false positives.
    /// </summary>
    /// <param name="episode">Episode to analyze.</param>
    /// <param name="percentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <returns>Search start position in seconds from the end of the file.</returns>
    private double FindSearchStart(QueuedEpisode episode, int percentage, int threshold)
    {
        ArgumentNullException.ThrowIfNull(episode);

        // Initial search parameters
        var searchStart = 3d * _config.MinimumCreditsDuration;
        var maxSearchStart = episode.Duration - episode.CreditsFingerprintStart;

        var stepSize = 2d * _config.MinimumCreditsDuration;

        while (searchStart < maxSearchStart)
        {
            var scanTime = episode.Duration - searchStart;

            var timeRange = new TimeRange(scanTime - 1.0, scanTime);

            var blackFrames = FFmpegWrapper.DetectBlackFrames(episode, timeRange, percentage, threshold);

            LogSearchScanning(_logger, scanTime, searchStart, blackFrames.Length);

            if (blackFrames.Length < 3)
            {
                // No black frames found, this is a good starting point
                LogFoundSearchStart(_logger, searchStart);
                return searchStart;
            }

            searchStart += stepSize;
        }

        LogMaxSearchDistanceReached(_logger, episode.Name, maxSearchStart);

        return maxSearchStart;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Analyzing {Count} episodes for credits using black frame detection")]
    private static partial void LogAnalyzingEpisodes(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No valid credits found for {Episode}")]
    private static partial void LogNoValidCreditsFound(ILogger logger, string episode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found credits for {Episode} at {Start:F2}s")]
    private static partial void LogFoundCredits(ILogger logger, string episode, double start);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error analyzing {Episode} for credits")]
    private static partial void LogErrorAnalyzingCredits(ILogger logger, Exception ex, string episode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Episode} at {Start:F2}s has {Count} black frames")]
    private static partial void LogBlackFramesDetected(ILogger logger, string episode, double start, int count);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Expanded search range: new lower limit = {Limit:F2}s")]
    private static partial void LogExpandedSearchLowerLimit(ILogger logger, double limit);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Expanded search range: new upper limit = {Limit:F2}s")]
    private static partial void LogExpandedSearchUpperLimit(ILogger logger, double limit);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during black frame analysis for {Episode}")]
    private static partial void LogErrorDuringAnalysis(ILogger logger, Exception ex, string episode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "No suitable chapters found for {Episode}")]
    private static partial void LogNoSuitableChaptersFound(ILogger logger, string episode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Chapter at {Start:F2}s has no black frames at start")]
    private static partial void LogChapterNoBlackFramesAtStart(ILogger logger, double start);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found credits using chapter marker at {Start:F2}s")]
    private static partial void LogFoundCreditsWithChapterMarker(ILogger logger, double start);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Search: scanning at {Position:F2}s ({DistanceFromEnd:F2}s from end), found {Count} black frames")]
    private static partial void LogSearchScanning(ILogger logger, double position, double distanceFromEnd, int count);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Found suitable search start at {DistanceFromEnd:F2}s from end")]
    private static partial void LogFoundSearchStart(ILogger logger, double distanceFromEnd);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Maximum distance reached when finding search start for {Episode}. Using {DistanceFromEnd:F2}s from end")]
    private static partial void LogMaxSearchDistanceReached(ILogger logger, string episode, double distanceFromEnd);
}
