// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
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
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="database">Segment database facade.</param>
public sealed partial class BlackFrameAnalyzer(ILogger<BlackFrameAnalyzer> logger, IFFmpegService ffmpegService, IIntroSkipperDatabase database) : IMediaFileAnalyzer
{
    /// <summary>
    /// Maximum distance, in seconds, between a chapter marker and the start of the black run
    /// containing it for the marker to still count as the start of the credits. This bounds the
    /// backward scan in <see cref="TryAnalyzeChaptersAsync"/> and tolerates pre-credits fades
    /// longer than the historical 5-second assumption without accepting markers placed deep
    /// inside unrelated black segments.
    /// </summary>
    private const double MaxChapterOffsetFromBlackRunStart = 15;

    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly TimeSpan _maximumError = TimeSpan.FromSeconds(4);
    private readonly ILogger<BlackFrameAnalyzer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IIntroSkipperDatabase _database = database;

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
            .Where(e => e.NeedsAnalysis(mode))
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
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // First try to use chapter markers if available
                var credit = _config.UseChapterMarkersBlackFrame
                    ? await TryAnalyzeChaptersAsync(episode, percentage, threshold, cancellationToken).ConfigureAwait(false)
                    : null;

                if (credit is null)
                {
                    // Reset searchStart if it exceeds the valid search range for this episode.
                    // This can happen when a previous longer episode sets a large searchStart that
                    // causes lowerLimit > upperLimit in AnalyzeMediaFileAsync, breaking the binary search.
                    var maxSearchDistance = episode.Duration - episode.CreditsFingerprintStart;
                    if (searchStart > maxSearchDistance)
                    {
                        searchStart = 0.0;
                    }

                    // If no suitable chapters found, use black frame detection
                    if (searchStart < _config.MinimumCreditsDuration)
                    {
                        searchStart = await FindSearchStartAsync(episode, percentage, threshold, cancellationToken).ConfigureAwait(false);
                    }

                    credit = await AnalyzeMediaFileAsync(
                        episode,
                        searchStart,
                        percentage,
                        threshold,
                        cancellationToken).ConfigureAwait(false);
                }

                if (credit is null || !credit.Valid)
                {
                    LogNoValidCreditsFound(_logger, episode.Name);
                    continue;
                }

                LogFoundCredits(_logger, episode.Name, credit.Start);

                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                await _database.UpdateTimestampAsync(credit, mode, configHash: episode.AnalysisConfigHash, cancellationToken: cancellationToken).ConfigureAwait(false);

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
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>Credits segment if found; otherwise null.</returns>
    public async Task<Segment?> AnalyzeMediaFileAsync(QueuedEpisode episode, double initialStart, int minimumBlackPercentage, int threshold, CancellationToken cancellationToken = default)
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
                var blackFrames = await _ffmpegService.DetectBlackFramesAsync(episode, timeRange, minimumBlackPercentage, threshold, AnalysisMode.Credits, cancellationToken).ConfigureAwait(false);

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
        catch (OperationCanceledException)
        {
            throw;
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
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>Credits segment if found using chapters; otherwise null.</returns>
    internal async Task<Segment?> TryAnalyzeChaptersAsync(QueuedEpisode episode, int percentage, int threshold, CancellationToken cancellationToken)
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
            return null;
        }

        // Chapters are sorted latest-first and only the latest suitable chapter is considered.
        // Iterating on to earlier chapters is deliberately avoided: when the real credits chapter
        // failed validation, the loop used to accept an earlier act-break chapter instead, shifting
        // the detected outro start minutes too early (see #889). If the latest chapter does not
        // validate, returning null lets the caller fall back to regular black-frame analysis.
        var chapterStart = suitableChapters[0];
        var chapterCreditsDuration = episode.Duration - chapterStart;
        var maximumCreditsDuration = episode.Category == QueuedMediaCategory.Movie
            ? _config.MaximumMovieCreditsDuration
            : _config.MaximumCreditsDuration;
        if (chapterCreditsDuration > maximumCreditsDuration)
        {
            return null;
        }

        // Check for black frames at chapter start
        var startRange = new TimeRange(chapterStart, chapterStart + 1);
        var hasBlackFramesAtStart = (await _ffmpegService.DetectBlackFramesAsync(
            episode,
            startRange,
            percentage,
            threshold,
            AnalysisMode.Credits,
            cancellationToken).ConfigureAwait(false)).Length > 0;

        if (!hasBlackFramesAtStart)
        {
            LogChapterNoBlackFramesAtStart(_logger, chapterStart);
            return null;
        }

        // Verify the chapter is near the beginning of a black run.
        // Walk backwards to find the first non-black second before the chapter marker.
        var scanStart = Math.Max(0, chapterStart - (MaxChapterOffsetFromBlackRunStart + 1));
        var foundBlackRunStart = false;
        for (var probeStart = chapterStart - 1; probeStart >= scanStart; probeStart -= 1)
        {
            var beforeRange = new TimeRange(probeStart, probeStart + 1);
            var hasBlackFramesBefore = (await _ffmpegService.DetectBlackFramesAsync(
                episode,
                beforeRange,
                percentage,
                threshold,
                AnalysisMode.Credits,
                cancellationToken).ConfigureAwait(false)).Length > 0;

            if (!hasBlackFramesBefore)
            {
                foundBlackRunStart = true;
                break;
            }
        }

        if (!foundBlackRunStart)
        {
            return null;
        }

        LogFoundCreditsWithChapterMarker(_logger, chapterStart);
        return new Segment(episode.EpisodeId, new TimeRange(chapterStart, episode.Duration));
    }

    /// <summary>
    /// Finds an optimal starting point for the credits search to avoid false positives.
    /// </summary>
    /// <param name="episode">Episode to analyze.</param>
    /// <param name="percentage">Minimum percentage of the frame that must be black.</param>
    /// <param name="threshold">Threshold for black frame detection.</param>
    /// <param name="cancellationToken">Token used to cancel FFmpeg probing.</param>
    /// <returns>Search start position in seconds from the end of the file.</returns>
    private async Task<double> FindSearchStartAsync(QueuedEpisode episode, int percentage, int threshold, CancellationToken cancellationToken)
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

            var blackFrames = await _ffmpegService.DetectBlackFramesAsync(episode, timeRange, percentage, threshold, AnalysisMode.Credits, cancellationToken).ConfigureAwait(false);

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
