// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
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
    private const double Epsilon = 1e-3; // 1 ms tolerance for floating point comparisons
    private readonly ILogger _logger = logger;
    private readonly PluginConfiguration _config = config;
    private readonly string _ffprobePath = Plugin.Instance?.FFprobePath ?? "ffprobe";

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
            _logger.LogError("Invalid configuration: EndSnapThreshold, AdjustWindowInward, or AdjustWindowOutward is negative. Using defaults.");
            return new Segment(episode.EpisodeId) { Start = originalIntro.Start, End = originalIntro.End };
        }

        bool useChapters = adjustIntroBasedOnChapters ?? _config.AdjustIntroBasedOnChapters;
        var duration = episode.Duration;
        var chapters = useChapters ? Plugin.Instance?.GetChapters(episode.EpisodeId) ?? [] : [];

        _logger.LogTrace(
            "{EpisodeId} {Name} original intro: {Start} - {End}",
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
            _logger.LogWarning("{EpisodeId} {Name}: Negative intro start {Start}, resetting to 0", episode.EpisodeId, episode.Name, rawStart);
            snapToEpisodeStart = true;
        }
        else if (IsWithinThreshold(rawStart, 0, _config.EndSnapThreshold))
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
            _logger.LogTrace(
                "{EpisodeId} {Name}: Snapping intro start to 0 (within threshold {Threshold}), skipping IntroStartOffset",
                episode.EpisodeId,
                episode.Name,
                _config.EndSnapThreshold);
            adjustedStart = 0;
        }
        else
        {
            // Apply configurable start offset only if we are not snapping to the episode start
            adjustedStart = Math.Clamp(adjustedStart + _config.IntroStartOffset, 0, duration);
        }

        double rawEnd = originalIntro.End;
        double adjustedEnd = rawEnd;
        if (IsWithinThreshold(duration - rawEnd, 0, _config.EndSnapThreshold))
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
                    _logger.LogTrace(
                        "{EpisodeId} {Name}: No suitable silence found for intro end in range {Start}-{End}",
                        episode.EpisodeId,
                        episode.Name,
                        silenceRange.Start,
                        silenceRange.End);
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
            _logger.LogWarning(
                "{EpisodeId} {Name}: Adjusted start time {Start} >= end time {End}, reverting to original",
                episode.EpisodeId,
                episode.Name,
                adjustedStart,
                adjustedEnd);
            return new Segment(episode.EpisodeId) { Start = originalIntro.Start, End = originalIntro.End };
        }

        _logger.LogTrace(
            "{EpisodeId} {Name} adjusted intro: {Start} - {End}",
            episode.EpisodeId,
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
    private static double GetChapterBoundary(IReadOnlyList<ChapterInfo> chapters, double referenceTime, TimeRange searchRange)
    {
        double bestTime = referenceTime;
        double minDiff = double.MaxValue;
        bool found = false;

        foreach (var chapter in chapters)
        {
            var chapterTime = TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds;
            if (IsInRange(chapterTime, searchRange.Start, searchRange.End))
            {
                // Found a candidate in range
                double diff = Math.Abs(chapterTime - referenceTime);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    bestTime = chapterTime;
                    found = true;
                }
            }
        }

        return found ? bestTime : referenceTime;
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
                _logger.LogTrace("{EpisodeId} {Name}: No silence detected", episode.EpisodeId, episode.Name);
                return currentEnd;
            }

            foreach (var currentRange in silence)
            {
                _logger.LogTrace(
                    "{EpisodeId} {Name} silence: {Start} - {End}",
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
            _logger.LogWarning("{EpisodeId} {Name}: Error detecting silence: {Error}", episode.EpisodeId, episode.Name, ex.Message);
        }

        return currentEnd;
    }

    /// <summary>
    /// Snaps a timestamp to the nearest keyframe within the search range.
    /// </summary>
    private double SnapToNearestKeyframe(QueuedEpisode episode, double time, TimeRange searchRange)
    {
        if (episode.Duration <= 0 || searchRange.End <= 0 || searchRange.End <= searchRange.Start)
        {
            return time;
        }

        var extractedKeyframes = TryExtractKeyframes(episode);
        if (extractedKeyframes is not null)
        {
            return SelectNearest(extractedKeyframes.Keyframes, time);
        }

        return time;
    }

    private KeyframeData? TryExtractKeyframes(QueuedEpisode episode)
    {
        if (episode.Path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            var matroska = TryExtractKeyframes(episode.Path, () => new MkvKeyframeExtractor());
            if (matroska?.Keyframes.Count > 0)
            {
                _logger.LogDebug("{EpisodeId} Extracted {Count} keyframes from MKV", episode.EpisodeId, matroska.Keyframes.Count);
                return matroska;
            }
        }

        return TryExtractKeyframes(episode.Path, () => new FfprobeKeyframeExtractor(_ffprobePath, _logger));
    }

    private IntroSkipper.Data.KeyframeData? TryExtractKeyframes(
        string episodePath,
        Func<IKeyframeExtractor> extractor)
    {
        try
        {
            return extractor().GetKeyframeData(episodePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{EpisodePath}: Failed to extract keyframes", episodePath);
            return null;
        }
    }

    private static double SelectNearest(IReadOnlyList<double> times, double reference)
    {
        if (times.Count == 0)
        {
            return reference;
        }

        int left = 0;
        int right = times.Count - 1;

        // Binary search for the closest value
        while (left < right)
        {
            int mid = left + ((right - left) / 2);

            if (times[mid] == reference)
            {
                return times[mid];
            }

            if (times[mid] < reference)
            {
                left = mid + 1;
            }
            else
            {
                right = mid;
            }
        }

        // At this point, left == right, check neighbors for nearest
        var nearest = times[left];
        var bestDiff = Math.Abs(times[left] - reference);

        if (left > 0)
        {
            var prevDiff = Math.Abs(times[left - 1] - reference);
            if (prevDiff < bestDiff)
            {
                nearest = times[left - 1];
                bestDiff = prevDiff;
            }
        }

        if (left < times.Count - 1)
        {
            var nextDiff = Math.Abs(times[left + 1] - reference);
            if (nextDiff < bestDiff)
            {
                nearest = times[left + 1];
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

    /// <summary>
    /// Checks if a value is within a threshold of a target, accounting for floating point precision.
    /// </summary>
    private static bool IsWithinThreshold(double value, double target, double threshold) =>
        value <= target + threshold + Epsilon;

    /// <summary>
    /// Checks if a value is within a range, accounting for floating point precision.
    /// </summary>
    private static bool IsInRange(double value, double rangeStart, double rangeEnd) =>
        value + Epsilon >= rangeStart && value - Epsilon <= rangeEnd;
}
