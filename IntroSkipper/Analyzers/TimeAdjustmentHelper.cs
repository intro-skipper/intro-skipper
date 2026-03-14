// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

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
public partial class TimeAdjustmentHelper(ILogger logger, PluginConfiguration config)
{
    private const double Epsilon = 1e-3; // 1 ms tolerance for floating point comparisons
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
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(originalIntro);

        // Config checks
        if (_config.EndSnapThreshold < 0 || _config.AdjustWindowInward < 0 || _config.AdjustWindowOutward < 0)
        {
            LogInvalidConfiguration(_logger);
            return new Segment(episode.EpisodeId) { Start = originalIntro.Start, End = originalIntro.End };
        }

        bool useChapters = adjustIntroBasedOnChapters ?? _config.AdjustIntroBasedOnChapters;
        var duration = episode.Duration;
        var chapters = useChapters ? Plugin.Instance?.GetChapters(episode.EpisodeId) ?? [] : [];

        LogOriginalIntro(
            _logger,
            episode.EpisodeId,
            episode.Name,
            originalIntro.Start,
            originalIntro.End);

        // Evaluate negativity and snap threshold against the raw start before any clamping
        double rawStart = originalIntro.Start;
        double adjustedStart = rawStart;
        bool snapToEpisodeStart = false;

        if (rawStart < 0)
        {
            LogNegativeIntroStart(_logger, episode.EpisodeId, episode.Name, rawStart);
            snapToEpisodeStart = true;
        }
        else if (rawStart <= _config.EndSnapThreshold + Epsilon)
        {
            // If the detected start is within threshold of episode start, snap
            snapToEpisodeStart = true;
        }
        else if (useChapters && chapters.Count > 0)
        {
            // Only adjust to chapter boundaries if we're not snapping to start
            var searchRange = GetSearchRange(rawStart, duration, _config.AdjustWindowOutward, _config.AdjustWindowInward);
            // Match the reference time to the range center to avoid mismatches
            adjustedStart = GetChapterBoundary(chapters, rawStart, searchRange);
        }

        if (snapToEpisodeStart)
        {
            // When snapping to episode start, do NOT apply IntroStartOffset
            LogSnappingIntroStart(_logger, episode.EpisodeId, episode.Name, _config.EndSnapThreshold);
            adjustedStart = 0;
        }
        else
        {
            // Apply configurable start offset only if we are not snapping to the episode start
            adjustedStart = Math.Clamp(adjustedStart + _config.IntroStartOffset, 0, duration);
        }

        double rawEnd = originalIntro.End;
        double adjustedEnd = rawEnd;
        if (rawEnd >= duration - _config.EndSnapThreshold - Epsilon)
        {
            adjustedEnd = duration;
        }
        else
        {
            if (useChapters && chapters.Count > 0)
            {
                var searchRange = GetSearchRange(adjustedEnd, duration, _config.AdjustWindowInward, _config.AdjustWindowOutward);
                adjustedEnd = GetChapterBoundary(chapters, adjustedEnd, searchRange);
            }

            adjustedEnd -= _config.IntroEndOffset;
            // Keep end inside media duration after offset
            adjustedEnd = Math.Clamp(adjustedEnd, 0, duration);

            var silenceRange = GetSearchRange(adjustedEnd, duration, _config.AdjustWindowInward, _config.AdjustWindowOutward);
            if (_config.AdjustIntroBasedOnSilence)
            {
                var silenceAdjusted = AdjustIntroEndBasedOnSilence(episode, adjustedEnd, silenceRange, _config.SilenceDetectionMinimumDuration);
                if (silenceAdjusted != adjustedEnd)
                {
                    adjustedEnd = silenceAdjusted;
                }
                else
                {
                    LogNoSilenceFound(_logger, episode.EpisodeId, episode.Name, silenceRange.Start, silenceRange.End);
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
            LogAdjustedStartAfterEnd(_logger, episode.EpisodeId, episode.Name, adjustedStart, adjustedEnd);
            return new Segment(episode.EpisodeId) { Start = originalIntro.Start, End = originalIntro.End };
        }

        LogAdjustedIntro(_logger, episode.EpisodeId, episode.Name, adjustedStart, adjustedEnd);

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
    private static double GetChapterBoundary(IReadOnlyList<ChapterInfo> chapters, double referenceTime, TimeRange searchRange)
    {
        // Collect candidate chapter times within the inclusive range
        var candidates = chapters
            .Select(c => TimeSpan.FromTicks(c.StartPositionTicks).TotalSeconds)
            .Where(t => t + Epsilon >= searchRange.Start && t - Epsilon <= searchRange.End)
            .ToList();

        if (candidates.Count == 0)
        {
            return referenceTime;
        }

        return SelectNearest(candidates, referenceTime);
    }

    /// <summary>
    /// Adjusts the intro end based on detected silence within the search range.
    /// </summary>
    private double AdjustIntroEndBasedOnSilence(QueuedEpisode episode, double currentEnd, TimeRange searchRange, double silenceDetectionMinimumDuration)
    {
        try
        {
            var silence = FFmpegWrapper.DetectSilence(episode, searchRange);
            if (silence is not { Length: > 0 })
            {
                LogNoSilenceDetected(_logger, episode.EpisodeId, episode.Name);
                return currentEnd;
            }

            foreach (var currentRange in silence)
            {
                LogSilenceDetected(
                    _logger,
                    episode.EpisodeId,
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
            LogErrorDetectingSilence(_logger, episode.EpisodeId, episode.Name, ex.Message);
        }

        return currentEnd;
    }

    /// <summary>
    /// Snaps a timestamp to the nearest keyframe within the search range.
    /// </summary>
    private static double SnapToNearestKeyframe(QueuedEpisode episode, double time, TimeRange searchRange)
    {
        var keyframes = FFmpegWrapper.DetectKeyFrames(episode, searchRange);
        return SelectNearest(keyframes, time);
    }

    /// <summary>
    /// Selects the value in candidates nearest to the reference; returns reference if no candidates.
    /// </summary>
    private static double SelectNearest(IEnumerable<double> candidates, double reference)
    {
        double nearest = reference;
        double best = double.MaxValue;

        foreach (var v in candidates)
        {
            double d = Math.Abs(v - reference);
            if (d < best)
            {
                best = d;
                nearest = v;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Gets a search range around a given time.
    /// </summary>
    private static TimeRange GetSearchRange(double time, double duration, double windowStart, double windowEnd) =>
        new(
            Math.Max(time - windowStart, 0),
            Math.Min(time + windowEnd, duration)
        );

    [LoggerMessage(Level = LogLevel.Error, Message = "Invalid configuration: EndSnapThreshold, AdjustWindowInward, or AdjustWindowOutward is negative. Using defaults.")]
    private static partial void LogInvalidConfiguration(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EpisodeId} {Name} original intro: {Start} - {End}")]
    private static partial void LogOriginalIntro(ILogger logger, Guid episodeId, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EpisodeId} {Name}: Negative intro start {Start}, resetting to 0")]
    private static partial void LogNegativeIntroStart(ILogger logger, Guid episodeId, string name, double start);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EpisodeId} {Name}: Snapping intro start to 0 (within threshold {Threshold}), skipping IntroStartOffset")]
    private static partial void LogSnappingIntroStart(ILogger logger, Guid episodeId, string name, double threshold);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EpisodeId} {Name}: No suitable silence found for intro end in range {Start}-{End}")]
    private static partial void LogNoSilenceFound(ILogger logger, Guid episodeId, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EpisodeId} {Name}: Adjusted start time {Start} >= end time {End}, reverting to original")]
    private static partial void LogAdjustedStartAfterEnd(ILogger logger, Guid episodeId, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EpisodeId} {Name} adjusted intro: {Start} - {End}")]
    private static partial void LogAdjustedIntro(ILogger logger, Guid episodeId, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EpisodeId} {Name}: No silence detected")]
    private static partial void LogNoSilenceDetected(ILogger logger, Guid episodeId, string name);

    [LoggerMessage(Level = LogLevel.Trace, Message = "{EpisodeId} {Name} silence: {Start} - {End}")]
    private static partial void LogSilenceDetected(ILogger logger, Guid episodeId, string name, double start, double end);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EpisodeId} {Name}: Error detecting silence: {Error}")]
    private static partial void LogErrorDetectingSilence(ILogger logger, Guid episodeId, string name, string error);
}
