using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using ConfusedPolarBear.Plugin.IntroSkipper.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Chapter name analyzer.
/// </summary>
public class ChapterAnalyzer : IMediaFileAnalyzer
{
    private ILogger<ChapterAnalyzer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterAnalyzer"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public ChapterAnalyzer(ILogger<ChapterAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ReadOnlyCollection<QueuedEpisode> AnalyzeMediaFiles(
        ReadOnlyCollection<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var skippableRanges = new Dictionary<Guid, Intro>();

        // Episode analysis queue.
        var episodeAnalysisQueue = new List<QueuedEpisode>(analysisQueue);

        var expression = mode == AnalysisMode.Introduction ?
            Plugin.Instance!.Configuration.ChapterAnalyzerIntroductionPattern :
            Plugin.Instance!.Configuration.ChapterAnalyzerEndCreditsPattern;

        if (string.IsNullOrWhiteSpace(expression))
        {
            return analysisQueue;
        }

        foreach (var episode in episodeAnalysisQueue.Where(e => !e.IsAnalyzed.Contains(mode)))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var skipRange = FindMatchingChapter(
                episode,
                new(Plugin.Instance.GetChapters(episode.EpisodeId)),
                expression,
                mode);

            if (skipRange is null)
            {
                continue;
            }

            skippableRanges.Add(episode.EpisodeId, skipRange);
            episode.AddAnalysisMode(mode);
        }

        Plugin.Instance.UpdateTimestamps(skippableRanges, mode);

        return episodeAnalysisQueue.AsReadOnly();
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
    public Intro? FindMatchingChapter(
        QueuedEpisode episode,
        Collection<ChapterInfo> chapters,
        string expression,
        AnalysisMode mode)
    {
        Intro? matchingChapter = null;

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        int minDuration = mode == AnalysisMode.Introduction ?
            config.MinimumIntroDuration :
            config.MinimumCreditsDuration;
        int maxDuration = mode == AnalysisMode.Introduction ?
            config.MaximumIntroDuration :
            config.MaximumCreditsDuration;

        if (chapters.Count == 0)
        {
            return null;
        }

        var matchingChapters = chapters
            .Select((chapter, index) => new { chapter, next = chapters.ElementAtOrDefault(index + 1) ?? new ChapterInfo { StartPositionTicks = TimeSpan.FromSeconds(episode.Duration).Ticks } })
            .Where(pair => !string.IsNullOrWhiteSpace(pair.chapter.Name))
            .Where(pair => IsValidTimeRange(pair.chapter, pair.next, minDuration, maxDuration))
            .Where(pair => Regex.IsMatch(pair.chapter.Name ?? string.Empty, expression, RegexOptions.None, TimeSpan.FromSeconds(1)))
            .Select(pair => new Intro(episode.EpisodeId, new TimeRange(
                TimeSpan.FromTicks(pair.chapter.StartPositionTicks).TotalSeconds,
                TimeSpan.FromTicks(pair.next.StartPositionTicks).TotalSeconds)))
            .ToList();

        if (matchingChapters.Count == 0)
        {
            return null;
        }

        matchingChapter = mode == AnalysisMode.Introduction
                ? matchingChapters.LastOrDefault()
                : matchingChapters.FirstOrDefault();

        return matchingChapter;
    }

    private bool IsValidTimeRange(ChapterInfo chapter, ChapterInfo next, int minDuration, int maxDuration)
    {
        var timeRange = new TimeRange(
            TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds,
            TimeSpan.FromTicks(next.StartPositionTicks).TotalSeconds);
        return timeRange.Duration >= minDuration && timeRange.Duration <= maxDuration;
    }
}
