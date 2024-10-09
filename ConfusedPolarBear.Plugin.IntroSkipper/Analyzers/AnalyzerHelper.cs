using System;
using System.Collections.Generic;
using System.Linq;
using ConfusedPolarBear.Plugin.IntroSkipper.Configuration;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
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
        IReadOnlyList<QueuedEpisode> episodes,
        IReadOnlyDictionary<Guid, Segment> originalIntros,
        AnalysisMode mode)
    {
        return episodes
            .Where(episode => originalIntros.TryGetValue(episode.EpisodeId, out var _))
            .ToDictionary(
                episode => episode.EpisodeId,
                episode => AdjustIntroForEpisode(episode, originalIntros[episode.EpisodeId], mode));
    }

    private Segment AdjustIntroForEpisode(QueuedEpisode episode, Segment originalIntro, AnalysisMode mode)
    {
        _logger.LogTrace("{Name} original intro: {Start} - {End}", episode.Name, originalIntro.Start, originalIntro.End);

        var adjustedIntro = new Segment(originalIntro);
        var originalIntroStart = new TimeRange(Math.Max(0, (int)originalIntro.Start - 5), (int)originalIntro.Start + 10);
        var originalIntroEnd = new TimeRange((int)originalIntro.End - 10, Math.Min(episode.Duration, (int)originalIntro.End + 5));

        if (!AdjustIntroBasedOnChapters(episode, adjustedIntro, originalIntroStart, originalIntroEnd) && mode == AnalysisMode.Introduction)
        {
            AdjustIntroBasedOnSilence(episode, adjustedIntro, originalIntroEnd);
        }

        return adjustedIntro;
    }

    private bool AdjustIntroBasedOnChapters(QueuedEpisode episode, Segment adjustedIntro, TimeRange originalIntroStart, TimeRange originalIntroEnd)
    {
        var chapterTimes = (Plugin.Instance?.GetChapters(episode.EpisodeId) ?? [])
            .Select(c => TimeSpan.FromTicks(c.StartPositionTicks).TotalSeconds)
            .Prepend(0).Append(episode.Duration).ToList();

        for (int i = 0; i < chapterTimes.Count - 1; i++)
        {
            var chapterStartSeconds = chapterTimes[i];
            var chapterEndSeconds = chapterTimes[i + 1];

            if (originalIntroStart.Start < chapterStartSeconds && chapterStartSeconds < originalIntroStart.End)
            {
                adjustedIntro.Start = chapterStartSeconds;
                _logger.LogTrace("{Name} chapter found close to intro start: {Start}", episode.Name, chapterStartSeconds);
            }

            if (originalIntroEnd.Start < chapterEndSeconds && chapterEndSeconds < originalIntroEnd.End)
            {
                adjustedIntro.End = chapterEndSeconds;
                _logger.LogTrace("{Name} chapter found close to intro end: {End}", episode.Name, chapterEndSeconds);
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
