// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Chapter name analyzer.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ChapterAnalyzer"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
public partial class ChapterAnalyzer(ILogger<ChapterAnalyzer> logger) : IMediaFileAnalyzer
{
    private readonly ILogger<ChapterAnalyzer> _logger = logger;
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var expression = mode switch
        {
            AnalysisMode.Introduction => _config.ChapterAnalyzerIntroductionPattern,
            AnalysisMode.Credits => _config.ChapterAnalyzerEndCreditsPattern,
            AnalysisMode.Recap => _config.ChapterAnalyzerRecapPattern,
            AnalysisMode.Preview => _config.ChapterAnalyzerPreviewPattern,
            AnalysisMode.Commercial => _config.ChapterAnalyzerCommercialPattern,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unexpected analysis mode: {mode}")
        };

        if (string.IsNullOrWhiteSpace(expression))
        {
            return analysisQueue;
        }

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config);

        var episodesWithoutIntros = analysisQueue.Where(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed).ToList();

        foreach (var episode in episodesWithoutIntros)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var skipRange = FindMatchingChapter(
                episode,
                Plugin.Instance!.GetChapters(episode.EpisodeId),
                expression,
                mode);

            if (skipRange is null || !skipRange.Valid)
            {
                continue;
            }

            skipRange = timeAdjustmentHelper.AdjustIntroTimes(episode, skipRange, false);

            episode.SetAnalyzed(mode, EpisodeState.Analyzed);
            await Plugin.Instance!.UpdateTimestampAsync(skipRange, mode, cancellationToken).ConfigureAwait(false);
        }

        return analysisQueue;
    }

    /// <summary>
    /// Searches a list of chapter names for one that matches the provided regular expression.
    /// Only public to allow for unit testing.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="chapters">Media item chapters.</param>
    /// <param name="expression">Regular expression pattern.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Intro object containing skippable time range, or null if no chapter matched.</returns>
    public Segment? FindMatchingChapter(
        QueuedEpisode episode,
        IReadOnlyList<ChapterInfo> chapters,
        string expression,
        AnalysisMode mode)
    {
        var count = chapters.Count;
        if (count == 0)
        {
            return null;
        }

        var reversed = mode == AnalysisMode.Credits || mode == AnalysisMode.Preview;
        var (minDuration, maxDuration) = GetBounds(mode, episode);

        // Check all chapters
        for (int i = reversed ? count - 1 : 0; reversed ? i >= 0 : i < count; i += reversed ? -1 : 1)
        {
            var chapter = chapters[i];
            var next = chapters.ElementAtOrDefault(i + 1) ??
                new ChapterInfo { StartPositionTicks = TimeSpan.FromSeconds(episode.Duration).Ticks }; // Since the ending credits chapter may be the last chapter in the file, append a virtual chapter.

            if (string.IsNullOrWhiteSpace(chapter.Name))
            {
                continue;
            }

            var currentRange = new TimeRange(
                TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds,
                TimeSpan.FromTicks(next.StartPositionTicks).TotalSeconds);

            var baseMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: Chapter \"{1}\" ({2} - {3})",
                    episode.Path,
                    chapter.Name,
                    currentRange.Start,
                    currentRange.End);

            if (currentRange.Duration < minDuration || currentRange.Duration > maxDuration)
            {
                LogIgnoringInvalidDuration(baseMessage);
                continue;
            }

            // Regex.IsMatch() is used here in order to allow the runtime to cache the compiled regex
            // between function invocations.
            var match = Regex.IsMatch(
                chapter.Name,
                expression,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));

            if (!match)
            {
                LogIgnoringNoRegexMatch(baseMessage);
                continue;
            }

            // Check if the next (or previous for Credits) chapter also matches
            var adjacentChapter = reversed ? chapters.ElementAtOrDefault(i - 1) : next;
            if (adjacentChapter != null && !string.IsNullOrWhiteSpace(adjacentChapter.Name))
            {
                // Check for possibility of overlapping keywords
                var overlap = Regex.IsMatch(
                    adjacentChapter.Name,
                    expression,
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1));

                if (overlap)
                {
                    LogIgnoringAdjacentMatch(baseMessage);
                    continue;
                }
            }

            LogChapterOk(baseMessage);
            return new Segment(episode.EpisodeId, currentRange);
        }

        return null;
    }

    private (double Min, double Max) GetBounds(AnalysisMode mode, QueuedEpisode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);

        if (_config.FullLengthChapters)
        {
            // Leave 1 second buffer at start and end
            return (1, episode.Duration - 1);
        }

        // Map analysis mode to duration bounds
        return mode switch
        {
            AnalysisMode.Introduction => (_config.MinimumIntroDuration, _config.MaximumIntroDuration),
            AnalysisMode.Credits => (_config.MinimumCreditsDuration,
                episode.Category == QueuedMediaCategory.Movie ? _config.MaximumMovieCreditsDuration : _config.MaximumCreditsDuration),
            AnalysisMode.Recap => (_config.MinimumRecapDuration, _config.MaximumRecapDuration),
            AnalysisMode.Preview => (_config.MinimumPreviewDuration, _config.MaximumPreviewDuration),
            AnalysisMode.Commercial => (_config.MinimumCommercialDuration, _config.MaximumCommercialDuration),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unsupported analysis mode: {mode}")
        };
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Base}: ignoring (invalid duration)")]
    private partial void LogIgnoringInvalidDuration(string @base);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Base}: ignoring (does not match regular expression)")]
    private partial void LogIgnoringNoRegexMatch(string @base);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Base}: ignoring (adjacent chapter also matches)")]
    private partial void LogIgnoringAdjacentMatch(string @base);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{Base}: okay")]
    private partial void LogChapterOk(string @base);
}
