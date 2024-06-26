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

        foreach (var episode in episodeAnalysisQueue.Where(e => !e.State.IsAnalyzed(mode)))
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
            episode.State.SetAnalyzed(mode, true);
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
        if (chapters.Count == 0)
        {
            return null;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var count = chapters.Count;
        var reversed = mode != AnalysisMode.Introduction;
        var (minDuration, maxDuration) = !reversed
            ? (config.MinimumIntroDuration, config.MaximumIntroDuration)
            : (config.MinimumCreditsDuration, config.MaximumCreditsDuration);

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
                _logger.LogTrace("{Base}: ignoring (invalid duration)", baseMessage);
                continue;
            }

            // Regex.IsMatch() is used here in order to allow the runtime to cache the compiled regex
            // between function invocations.
            var match = Regex.IsMatch(
                chapter.Name,
                expression,
                RegexOptions.None,
                TimeSpan.FromSeconds(1));

            if (!match)
            {
                _logger.LogTrace("{Base}: ignoring (does not match regular expression)", baseMessage);
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
                    _logger.LogTrace("{Base}: ignoring (adjacent chapter also matches)", baseMessage);
                    continue;
                }
            }

            _logger.LogTrace("{Base}: okay", baseMessage);
            return new Intro(episode.EpisodeId, currentRange);
        }

        return null;
    }
}
