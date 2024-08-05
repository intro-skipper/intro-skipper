using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ConfusedPolarBear.Plugin.IntroSkipper.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Analyzer Helper.
/// </summary>
public class AnalyzerHelper
{
    private readonly ILogger _logger;
    private readonly double silenceDetectionMinimumDuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyzerHelper"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public AnalyzerHelper(ILogger logger)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        silenceDetectionMinimumDuration = config.SilenceDetectionMinimumDuration;
        _logger = logger;
    }

    /// <summary>
    /// Adjusts the end timestamps of all intros so that they end at silence.
    /// </summary>
    /// <param name="episodes">QueuedEpisodes to adjust.</param>
    /// <param name="originalIntros">Original introductions.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Modified Intro Timestamps.</returns>
    public Dictionary<Guid, Intro> AdjustIntroTimes(
            ReadOnlyCollection<QueuedEpisode> episodes,
            Dictionary<Guid, Intro> originalIntros,
            AnalysisMode mode)
        {
            var modifiedIntros = new Dictionary<Guid, Intro>();

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

    private Intro AdjustIntroForEpisode(QueuedEpisode episode, Intro originalIntro, AnalysisMode mode)
    {
        var chapters = GetChaptersWithVirtualEnd(episode);
        var adjustedIntro = new Intro(originalIntro);

        var originalIntroStart = new TimeRange(Math.Max(0, originalIntro.IntroStart - 5), originalIntro.IntroStart + 10);
        var originalIntroEnd = new TimeRange(originalIntro.IntroEnd - 10, Math.Min(episode.Duration, originalIntro.IntroEnd + 5));

        _logger.LogTrace("{Name} original intro: {Start} - {End}", episode.Name, originalIntro.IntroStart, originalIntro.IntroEnd);

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

    private bool AdjustIntroBasedOnChapters(QueuedEpisode episode, List<ChapterInfo> chapters, Intro adjustedIntro, TimeRange originalIntroStart, TimeRange originalIntroEnd)
    {
        foreach (var chapter in chapters)
        {
            var chapterStartSeconds = TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds;

            if (originalIntroStart.Start < chapterStartSeconds && chapterStartSeconds < originalIntroStart.End)
            {
                adjustedIntro.IntroStart = chapterStartSeconds;
                _logger.LogTrace("{Name} chapter found close to intro start: {Start}", episode.Name, chapterStartSeconds);
            }

            if (originalIntroEnd.Start < chapterStartSeconds && chapterStartSeconds < originalIntroEnd.End)
            {
                adjustedIntro.IntroEnd = chapterStartSeconds;
                _logger.LogTrace("{Name} chapter found close to intro end: {End}", episode.Name, chapterStartSeconds);
                return true;
            }
        }

        return false;
    }

    private void AdjustIntroBasedOnSilence(QueuedEpisode episode, Intro adjustedIntro, TimeRange originalIntroEnd)
    {
        var silence = FFmpegWrapper.DetectSilence(episode, originalIntroEnd);

        foreach (var currentRange in silence)
        {
            _logger.LogTrace("{Name} silence: {Start} - {End}", episode.Name, currentRange.Start, currentRange.End);

            if (IsValidSilenceForIntroAdjustment(currentRange, originalIntroEnd, adjustedIntro))
            {
                adjustedIntro.IntroEnd = currentRange.Start;
                break;
            }
        }
    }

    private bool IsValidSilenceForIntroAdjustment(TimeRange silenceRange, TimeRange originalIntroEnd, Intro adjustedIntro)
    {
        return originalIntroEnd.Intersects(silenceRange) &&
                silenceRange.Duration >= silenceDetectionMinimumDuration &&
                silenceRange.Start >= adjustedIntro.IntroStart;
    }
}
