using System;
using System.Collections.Generic;
using System.Linq;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Helper class for adjusting intro times.
/// </summary>
public class TimeAdjustmentHelper(ILogger logger, PluginConfiguration config)
{
    private readonly ILogger _logger = logger;
    private readonly PluginConfiguration _config = config;

    /// <summary>
    /// Adjusts the intro times of an episode and returns a new Segment with the adjusted times.
    /// </summary>
    /// <param name="episode">The episode to adjust.</param>
    /// <param name="originalIntro">The original intro segment.</param>
    /// <param name="adjustIntroBasedOnChapters">Whether to adjust based on chapters (overrides _config if true).</param>
    /// <returns>A new Segment with adjusted intro times.</returns>
    /// <exception cref="ArgumentNullException">Thrown if episode or originalIntro is null.</exception>
    public Segment AdjustIntroTimes(
        QueuedEpisode episode,
        Segment originalIntro,
        bool? adjustIntroBasedOnChapters = null)
    {
        bool useChapters = adjustIntroBasedOnChapters ?? _config.AdjustIntroBasedOnChapters;
        var duration = episode.Duration;
        var chapters = useChapters ? Plugin.Instance?.GetChapters(episode.EpisodeId) ?? [] : [];

        _logger.LogTrace(
            "{Name} original intro: {Start} - {End}",
            episode.Name,
            originalIntro.Start,
            originalIntro.End);

        double adjustedStart = originalIntro.Start;
        if (adjustedStart < _config.EndSnapThreshold)
        {
            adjustedStart = 0;
        }
        else if (useChapters && chapters.Count > 0)
        {
            var searchRange = GetSearchRange(originalIntro.Start, duration, _config.AdjustWindowBefore, _config.AdjustWindowAfter);
            adjustedStart = GetChapterBoundary(chapters, originalIntro.Start, searchRange);
        }

        // Apply _configurable start offset after all other start logic
        adjustedStart += _config.SecondsOfIntroStartToPlay;

        double adjustedEnd = originalIntro.End;
        if (adjustedEnd > duration - _config.EndSnapThreshold)
        {
            adjustedEnd = duration;
        }
        else
        {
            if (useChapters && chapters.Count > 0)
            {
                var searchRange = GetSearchRange(adjustedEnd, duration, _config.AdjustWindowBefore, _config.AdjustWindowAfter);
                adjustedEnd = GetChapterBoundary(chapters, adjustedEnd, searchRange);
            }

            adjustedEnd += _config.RemainingSecondsOfIntro;

            var silenceRange = GetSearchRange(adjustedEnd, duration, _config.AdjustWindowBefore, _config.AdjustWindowAfter);
            if (_config.AdjustIntroBasedOnSilence)
            {
                var silenceAdjusted = AdjustIntroEndBasedOnSilence(episode, adjustedEnd, silenceRange, _config.SilenceDetectionMinimumDuration);
                if (silenceAdjusted != adjustedEnd)
                {
                    adjustedEnd = silenceAdjusted;
                }
                else
                {
                    _logger.LogTrace("No suitable silence found for intro end in range {Start}-{End}", silenceRange.Start, silenceRange.End);
                }
            }

            if (_config.SnapToKeyframe)
            {
                adjustedEnd = SnapToNearestKeyframe(episode, adjustedEnd, silenceRange);
            }
        }

        // Ensure start < end after all adjustments
        if (adjustedStart >= adjustedEnd)
        {
            _logger.LogWarning("{Name}: Adjusted start time {Start} >= end time {End}, reverting to original", episode.Name, adjustedStart, adjustedEnd);
            return new Segment(episode.EpisodeId) { Start = originalIntro.Start, End = originalIntro.End };
        }

        _logger.LogTrace(
            "{Name} adjusted intro: {Start} - {End}",
            episode.Name,
            adjustedStart,
            adjustedEnd);

        return new Segment(episode.EpisodeId)
        {
            Start = adjustedStart,
            End = adjustedEnd
        };
    }

    /// <summary>
    /// Finds the chapter boundary (start time in seconds) within the given search range.
    /// Returns currentEnd if no chapter is found.
    /// </summary>
    private static double GetChapterBoundary(IReadOnlyList<ChapterInfo> chapters, double currentEnd, TimeRange searchRange)
    {
        foreach (var chapter in chapters)
        {
            var chapterTime = TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds;
            if (chapterTime > searchRange.Start && chapterTime < searchRange.End)
            {
                return chapterTime;
            }
        }

        return currentEnd;
    }

    /// <summary>
    /// Adjusts the intro end based on detected silence within the search range.
    /// </summary>
    private double AdjustIntroEndBasedOnSilence(QueuedEpisode episode, double currentEnd, TimeRange searchRange, double silenceDetectionMinimumDuration)
    {
        try
        {
            var silence = FFmpegWrapper.DetectSilence(episode, searchRange);
            if (silence.Length == 0)
            {
                _logger.LogTrace("No silence detected for {Name}", episode.Name);
                return currentEnd;
            }

            foreach (var currentRange in silence)
            {
                _logger.LogTrace(
                    "{Name} silence: {Start} - {End}",
                    episode.Name,
                    currentRange.Start,
                    currentRange.End);
                if (
                    !searchRange.Intersects(currentRange) ||
                    currentRange.Duration < silenceDetectionMinimumDuration ||
                    currentRange.Start < searchRange.Start)
                {
                    continue;
                }

                return currentRange.Start;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error detecting silence: {Error}", ex.Message);
        }

        return currentEnd;
    }

    /// <summary>
    /// Snaps a timestamp to the nearest keyframe within the search range.
    /// </summary>
    private static double SnapToNearestKeyframe(QueuedEpisode episode, double time, TimeRange searchRange)
    {
        var keyframes = FFmpegWrapper.DetectKeyFrames(episode, searchRange);
        return keyframes
            .OrderBy(kf => Math.Abs(kf - time))
            .DefaultIfEmpty(time)
            .First();
    }

    /// <summary>
    /// Gets a search range around a given time.
    /// </summary>
    private static TimeRange GetSearchRange(double time, double duration, double windowBefore, double windowAfter)
    {
        return new TimeRange(
            Math.Max(time - windowBefore, 0),
            Math.Min(time + windowAfter, duration));
    }
}
