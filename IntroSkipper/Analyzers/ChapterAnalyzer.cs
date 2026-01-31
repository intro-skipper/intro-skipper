// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
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
    public Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        ICollection<AnalyzedSegment> analyzedSegments,
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
            return Task.FromResult(analysisQueue);
        }

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config);

        var episodesWithoutIntros = analysisQueue.Where(e => !e.GetAnalyzed(mode)).ToList();

        foreach (var episode in episodesWithoutIntros)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var matchingSegments = FindMatchingChapters(
                episode,
                Plugin.Instance!.GetChapters(episode.EpisodeId),
                expression,
                mode);

            if (matchingSegments.Count == 0)
            {
                continue;
            }

            // Merge adjacent matching chapters (within MaximumTimeSkip)
            var mergedRanges = TimeRangeHelpers.MergeOverlappingRanges(
                matchingSegments.Select(s => new TimeRange(s.Start, s.End)),
                _config.MaximumTimeSkip);

            // Adjust times and add all merged segments
            foreach (var range in mergedRanges)
            {
                var mergedSegment = new Segment(episode.EpisodeId, range, episode.SeasonId);
                var adjustedSegment = timeAdjustmentHelper.AdjustIntroTimes(episode, mergedSegment, false);
                analyzedSegments.Add(new AnalyzedSegment(adjustedSegment));
            }

            episode.SetAnalyzed(mode, true);
        }

        return Task.FromResult(analysisQueue);
    }

    /// <summary>
    /// Searches a list of chapter names for all that match the provided regular expression.
    /// Only public to allow for unit testing.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="chapters">Media item chapters.</param>
    /// <param name="expression">Regular expression pattern.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>List of segments for all matching chapters.</returns>
    public IReadOnlyList<Segment> FindMatchingChapters(
        QueuedEpisode episode,
        IReadOnlyList<ChapterInfo> chapters,
        string expression,
        AnalysisMode mode)
    {
        var matchingSegments = new List<Segment>();
        var count = chapters.Count;

        if (count == 0)
        {
            return matchingSegments;
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

            LogChapterOk(baseMessage);
            matchingSegments.Add(new Segment(episode.EpisodeId, currentRange, episode.SeasonId));
        }

        return matchingSegments;
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
