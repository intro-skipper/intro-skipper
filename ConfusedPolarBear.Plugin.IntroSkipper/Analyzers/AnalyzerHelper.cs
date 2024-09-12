using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ConfusedPolarBear.Plugin.IntroSkipper.Configuration;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Analyzer Helper.
/// </summary>
public class AnalyzerHelper
{
    private readonly ILogger _logger;
    private readonly double _silenceDetectionMinimumDuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyzerHelper"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public AnalyzerHelper(ILogger logger)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _silenceDetectionMinimumDuration = config.SilenceDetectionMinimumDuration;
        _logger = logger;
    }

    /// <summary>
    /// Adjusts the end timestamps of all intros so that they end at silence.
    /// </summary>
    /// <param name="episodes">QueuedEpisodes to adjust.</param>
    /// <param name="originalIntros">Original introductions.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Modified Intro Timestamps.</returns>
    public Dictionary<Guid, Segment> AdjustIntroTimes(
            ReadOnlyCollection<QueuedEpisode> episodes,
            Dictionary<Guid, Segment> originalIntros,
            AnalysisMode mode)
        {
            var modifiedIntros = new Dictionary<Guid, Segment>();

            foreach (var episode in episodes)
            {
                _logger.LogTrace("Adjusting introduction end time for {Name} ({Id})", episode.Name, episode.EpisodeId);

                if (!originalIntros.TryGetValue(episode.EpisodeId, out var originalIntro))
                {
                    _logger.LogTrace("{Name} does not have an intro", episode.Name);
                    continue;
                }

                var adjustedIntro = AdjustIntroForEpisode(episode, originalIntro, mode);
                modifiedIntros[episode.EpisodeId] = adjustedIntro;
            }

            return modifiedIntros;
        }

    private Segment AdjustIntroForEpisode(QueuedEpisode episode, Segment originalIntro, AnalysisMode mode)
    {
        var chapters = GetChaptersWithVirtualEnd(episode);
        var adjustedIntro = new Segment(originalIntro);

        var originalIntroStart = new TimeRange(Math.Max(0, (int)originalIntro.Start - 5), (int)originalIntro.Start + 10);
        var originalIntroEnd = new TimeRange((int)originalIntro.End - 10, Math.Min(episode.Duration, (int)originalIntro.End + 5));

        _logger.LogTrace("{Name} original intro: {Start} - {End}", episode.Name, originalIntro.Start, originalIntro.End);

        if (!AdjustIntroBasedOnChapters(episode, chapters, adjustedIntro, originalIntroStart, originalIntroEnd)
            && mode == AnalysisMode.Introduction)
        {
            AdjustIntroBasedOnSilence(episode, adjustedIntro, originalIntroEnd);
        }

        return adjustedIntro;
    }

    private List<ChapterInfo> GetChaptersWithVirtualEnd(QueuedEpisode episode)
    {
        var chapters = Plugin.Instance?.GetChapters(episode.EpisodeId) ?? new List<ChapterInfo>();
        chapters.Add(new ChapterInfo { StartPositionTicks = TimeSpan.FromSeconds(episode.Duration).Ticks });
        return chapters;
    }

    private bool AdjustIntroBasedOnChapters(QueuedEpisode episode, List<ChapterInfo> chapters, Segment adjustedIntro, TimeRange originalIntroStart, TimeRange originalIntroEnd)
    {
        foreach (var chapter in chapters)
        {
            var chapterStartSeconds = TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds;

            if (originalIntroStart.Start < chapterStartSeconds && chapterStartSeconds < originalIntroStart.End)
            {
                adjustedIntro.Start = chapterStartSeconds;
                _logger.LogTrace("{Name} chapter found close to intro start: {Start}", episode.Name, chapterStartSeconds);
            }

            if (originalIntroEnd.Start < chapterStartSeconds && chapterStartSeconds < originalIntroEnd.End)
            {
                adjustedIntro.End = chapterStartSeconds;
                _logger.LogTrace("{Name} chapter found close to intro end: {End}", episode.Name, chapterStartSeconds);
                return true;
            }
        }

        return false;
    }

    private void AdjustIntroBasedOnSilence(QueuedEpisode episode, Segment adjustedIntro, TimeRange originalIntroEnd)
    {
        var silence = FFmpegWrapper.DetectSilence(episode, originalIntroEnd);

        foreach (var currentRange in silence)
        {
            _logger.LogTrace("{Name} silence: {Start} - {End}", episode.Name, currentRange.Start, currentRange.End);

            if (IsValidSilenceForIntroAdjustment(currentRange, originalIntroEnd, adjustedIntro))
            {
                adjustedIntro.End = currentRange.Start;
                break;
            }
        }
    }

    private bool IsValidSilenceForIntroAdjustment(TimeRange silenceRange, TimeRange originalIntroEnd, Segment adjustedIntro)
    {
        return originalIntroEnd.Intersects(silenceRange) &&
                silenceRange.Duration >= _silenceDetectionMinimumDuration &&
                silenceRange.Start >= adjustedIntro.Start;
    }
}
