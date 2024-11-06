// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers
{
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
        public IReadOnlyList<Segment> AdjustIntroTimes(
            IReadOnlyList<QueuedEpisode> episodes,
            IReadOnlyList<Segment> originalIntros,
            AnalysisMode mode)
        {
            return originalIntros.Select(i => AdjustIntroForEpisode(episodes.FirstOrDefault(e => originalIntros.Any(i => i.EpisodeId == e.EpisodeId)), i, mode)).ToList();
        }

        private Segment AdjustIntroForEpisode(QueuedEpisode? episode, Segment originalIntro, AnalysisMode mode)
        {
            if (episode is null)
            {
                return new Segment(originalIntro.EpisodeId);
            }

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
            var chapters = Plugin.Instance?.GetChapters(episode.EpisodeId) ?? [];
            double previousTime = 0;

            for (int i = 0; i <= chapters.Count; i++)
            {
                double currentTime = i < chapters.Count
                    ? TimeSpan.FromTicks(chapters[i].StartPositionTicks).TotalSeconds
                    : episode.Duration;

                if (originalIntroStart.Start < previousTime && previousTime < originalIntroStart.End)
                {
                    adjustedIntro.Start = previousTime;
                    _logger.LogTrace("{Name} chapter found close to intro start: {Start}", episode.Name, previousTime);
                }

                if (originalIntroEnd.Start < currentTime && currentTime < originalIntroEnd.End)
                {
                    adjustedIntro.End = currentTime;
                    _logger.LogTrace("{Name} chapter found close to intro end: {End}", episode.Name, currentTime);
                    return true;
                }

                previousTime = currentTime;
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
}
