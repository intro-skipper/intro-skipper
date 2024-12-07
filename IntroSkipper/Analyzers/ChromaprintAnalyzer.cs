// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Initializes a new instance of the <see cref="ChromaprintAnalyzer"/> class.
/// </summary>
/// <param name="logger">Logger.</param>
public class ChromaprintAnalyzer(ILogger<ChromaprintAnalyzer> logger) : IMediaFileAnalyzer
{
    /// <summary>
    /// Seconds of audio in one fingerprint point.
    /// This value is defined by the Chromaprint library and should not be changed.
    /// </summary>
    private const double SamplesToSeconds = 0.1238;
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<ChromaprintAnalyzer> _logger = logger;
    private readonly Dictionary<Guid, Dictionary<uint, int>> _invertedIndexCache = [];
    private AnalysisMode _analysisMode;

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        // Episodes that were not analyzed.
        var episodeAnalysisQueue = analysisQueue.Where(e => !e.IsAnalyzed).ToList();

        if (episodeAnalysisQueue.Count <= 1)
        {
            return analysisQueue;
        }

        _analysisMode = mode;

        // All intros for this season.
        var seasonIntros = new Dictionary<Guid, Segment>();

        // Cache of all fingerprints for this season.
        var fingerprintCache = new Dictionary<Guid, uint[]>();

        // Compute fingerprints for all episodes in the season
        foreach (var episode in episodeAnalysisQueue)
        {
            try
            {
                fingerprintCache[episode.EpisodeId] = FFmpegWrapper.Fingerprint(episode, mode);

                // Use reversed fingerprints for credits
                if (_analysisMode == AnalysisMode.Credits)
                {
                    Array.Reverse(fingerprintCache[episode.EpisodeId]);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return analysisQueue;
                }
            }
            catch (FingerprintException ex)
            {
                _logger.LogDebug("Caught fingerprint error: {Ex}", ex);
                WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);

                // Fallback to an empty fingerprint on any error
                fingerprintCache[episode.EpisodeId] = [];
            }
        }

        // While there are still episodes in the queue
        while (episodeAnalysisQueue.Count > 0)
        {
            // Pop the first episode from the queue
            var currentEpisode = episodeAnalysisQueue[0];
            episodeAnalysisQueue.RemoveAt(0);

            // Search through all remaining episodes.
            foreach (var remainingEpisode in episodeAnalysisQueue)
            {
                // Compare the current episode to all remaining episodes in the queue.
                var (currentIntro, remainingIntro) = CompareEpisodes(
                    currentEpisode.EpisodeId,
                    fingerprintCache[currentEpisode.EpisodeId],
                    remainingEpisode.EpisodeId,
                    fingerprintCache[remainingEpisode.EpisodeId]);

                // Ignore this comparison result if:
                // - one of the intros isn't valid, or
                // - the introduction exceeds the configured limit
                if (
                    !remainingIntro.Valid ||
                    (_analysisMode == AnalysisMode.Introduction && remainingIntro.Duration > Plugin.Instance!.Configuration.MaximumIntroDuration))
                {
                    continue;
                }

                /* Since the Fingerprint() function returns an array of Chromaprint points without time
                 * information, the times reported from the index search function start from 0.
                 *
                 * While this is desired behavior for detecting introductions, it breaks credit
                 * detection, as the audio we're analyzing was extracted from some point into the file.
                 *
                 * To fix this, the starting and ending times need to be switched, as they were previously reversed
                 * and subtracted from the episode duration to get the reported time range.
                 */
                if (_analysisMode == AnalysisMode.Credits)
                {
                    // Calculate new values for the current intro
                    double currentOriginalIntroStart = currentIntro.Start;
                    currentIntro.Start = currentEpisode.Duration - currentIntro.End;
                    currentIntro.End = currentEpisode.Duration - currentOriginalIntroStart;

                    // Calculate new values for the remaining intro
                    double remainingIntroOriginalStart = remainingIntro.Start;
                    remainingIntro.Start = remainingEpisode.Duration - remainingIntro.End;
                    remainingIntro.End = remainingEpisode.Duration - remainingIntroOriginalStart;
                }

                // Only save the discovered intro if it is:
                // - the first intro discovered for this episode
                // - longer than the previously discovered intro
                if (
                    !seasonIntros.TryGetValue(currentIntro.EpisodeId, out var savedCurrentIntro) ||
                    currentIntro.Duration > savedCurrentIntro.Duration)
                {
                    seasonIntros[currentIntro.EpisodeId] = currentIntro;
                }

                if (
                    !seasonIntros.TryGetValue(remainingIntro.EpisodeId, out var savedRemainingIntro) ||
                    remainingIntro.Duration > savedRemainingIntro.Duration)
                {
                    seasonIntros[remainingIntro.EpisodeId] = remainingIntro;
                }

                break;
            }

            // If an intro is found for this episode, adjust its times and save it else add it to the list of episodes without intros.
            if (!seasonIntros.TryGetValue(currentEpisode.EpisodeId, out var intro))
            {
                continue;
            }

            currentEpisode.IsAnalyzed = true;
            await Plugin.Instance!.UpdateTimestampAsync(intro, mode).ConfigureAwait(false);
        }

        return analysisQueue;
    }

    /// <summary>
    /// Analyze two episodes to find an introduction sequence shared between them.
    /// </summary>
    /// <param name="lhsId">First episode id.</param>
    /// <param name="lhsPoints">First episode fingerprint points.</param>
    /// <param name="rhsId">Second episode id.</param>
    /// <param name="rhsPoints">Second episode fingerprint points.</param>
    /// <returns>Intros for the first and second episodes.</returns>
    public (Segment Lhs, Segment Rhs) CompareEpisodes(
        Guid lhsId,
        uint[] lhsPoints,
        Guid rhsId,
        uint[] rhsPoints)
    {
        // Creates an inverted fingerprint point index for both episodes.
        // For every point which is a 100% match, search for an introduction at that point.
        var (lhsRanges, rhsRanges) = SearchInvertedIndex(lhsId, lhsPoints, rhsId, rhsPoints);

        if (lhsRanges.Count > 0)
        {
            _logger.LogTrace("Index search successful");

            return GetLongestTimeRange(lhsId, lhsRanges, rhsId, rhsRanges);
        }

        _logger.LogTrace(
            "Unable to find a shared introduction sequence between {LHS} and {RHS}",
            lhsId,
            rhsId);

        return (new Segment(lhsId), new Segment(rhsId));
    }

    /// <summary>
    /// Locates the longest range of similar audio and returns an Intro class for each range.
    /// </summary>
    /// <param name="lhsId">First episode id.</param>
    /// <param name="lhsRanges">First episode shared timecodes.</param>
    /// <param name="rhsId">Second episode id.</param>
    /// <param name="rhsRanges">Second episode shared timecodes.</param>
    /// <returns>Intros for the first and second episodes.</returns>
    private static (Segment Lhs, Segment Rhs) GetLongestTimeRange(
        Guid lhsId,
        List<TimeRange> lhsRanges,
        Guid rhsId,
        List<TimeRange> rhsRanges)
    {
        // Store the longest time range as the introduction.
        lhsRanges.Sort();
        rhsRanges.Sort();

        var lhsIntro = lhsRanges[0];
        var rhsIntro = rhsRanges[0];

        // If the intro starts early in the episode, move it to the beginning.
        if (lhsIntro.Start <= 5)
        {
            lhsIntro.Start = 0;
        }

        if (rhsIntro.Start <= 5)
        {
            rhsIntro.Start = 0;
        }

        // Create Intro classes for each time range.
        return (new Segment(lhsId, lhsIntro), new Segment(rhsId, rhsIntro));
    }

    /// <summary>
    /// Search for a shared introduction sequence using inverted indexes.
    /// </summary>
    /// <param name="lhsId">LHS ID.</param>
    /// <param name="lhsPoints">Left episode fingerprint points.</param>
    /// <param name="rhsId">RHS ID.</param>
    /// <param name="rhsPoints">Right episode fingerprint points.</param>
    /// <returns>List of shared TimeRanges between the left and right episodes.</returns>
    private (List<TimeRange> Lhs, List<TimeRange> Rhs) SearchInvertedIndex(
        Guid lhsId,
        uint[] lhsPoints,
        Guid rhsId,
        uint[] rhsPoints)
    {
        var lhsRanges = new List<TimeRange>();
        var rhsRanges = new List<TimeRange>();

        // Generate inverted indexes for the left and right episodes.
        var lhsIndex = CreateInvertedIndex(lhsId, lhsPoints);
        var rhsIndex = CreateInvertedIndex(rhsId, rhsPoints);
        var indexShifts = new HashSet<int>();

        // For all audio points in the left episode, check if the right episode has a point which matches exactly.
        // If an exact match is found, calculate the shift that must be used to align the points.
        foreach (var kvp in lhsIndex)
        {
            var originalPoint = kvp.Key;

            for (var i = -1 * _config.InvertedIndexShift; i <= _config.InvertedIndexShift; i++)
            {
                var modifiedPoint = (uint)(originalPoint + i);

                if (rhsIndex.TryGetValue(modifiedPoint, out var rhsModifiedPoint))
                {
                    var lhsFirst = lhsIndex[originalPoint];
                    var rhsFirst = rhsModifiedPoint;
                    indexShifts.Add(rhsFirst - lhsFirst);
                }
            }
        }

        // Use all discovered shifts to compare the episodes.
        foreach (var shift in indexShifts)
        {
            var (lhsIndexContiguous, rhsIndexContiguous) = FindContiguous(lhsPoints, rhsPoints, shift);
            if (lhsIndexContiguous.End > 0 && rhsIndexContiguous.End > 0)
            {
                lhsRanges.Add(lhsIndexContiguous);
                rhsRanges.Add(rhsIndexContiguous);
            }
        }

        return (lhsRanges, rhsRanges);
    }

    /// <summary>
    /// Finds the longest contiguous region of similar audio between two fingerprints using the provided shift amount.
    /// </summary>
    /// <param name="lhs">First fingerprint to compare.</param>
    /// <param name="rhs">Second fingerprint to compare.</param>
    /// <param name="shiftAmount">Amount to shift one fingerprint by.</param>
    private (TimeRange Lhs, TimeRange Rhs) FindContiguous(
        uint[] lhs,
        uint[] rhs,
        int shiftAmount)
    {
        var leftOffset = 0;
        var rightOffset = 0;

        // Calculate the offsets for the left and right hand sides.
        if (shiftAmount < 0)
        {
            leftOffset -= shiftAmount;
        }
        else
        {
            rightOffset += shiftAmount;
        }

        // Store similar times for both LHS and RHS.
        var lhsTimes = new List<double>();
        var rhsTimes = new List<double>();
        var upperLimit = Math.Min(lhs.Length, rhs.Length) - Math.Abs(shiftAmount);

        // XOR all elements in LHS and RHS, using the shift amount from above.
        for (var i = 0; i < upperLimit; i++)
        {
            // XOR both samples at the current position.
            var lhsPosition = i + leftOffset;
            var rhsPosition = i + rightOffset;
            var diff = lhs[lhsPosition] ^ rhs[rhsPosition];

            // If the difference between the samples is small, flag both times as similar.
            if (CountBits(diff) > _config.MaximumFingerprintPointDifferences)
            {
                continue;
            }

            var lhsTime = lhsPosition * SamplesToSeconds;
            var rhsTime = rhsPosition * SamplesToSeconds;

            lhsTimes.Add(lhsTime);
            rhsTimes.Add(rhsTime);
        }

        // Ensure the last timestamp is checked
        lhsTimes.Add(double.MaxValue);
        rhsTimes.Add(double.MaxValue);

        // Now that both fingerprints have been compared at this shift, see if there's a contiguous time range.
        var lContiguous = TimeRangeHelpers.FindContiguous([.. lhsTimes], _config.MaximumTimeSkip);
        if (lContiguous is null || lContiguous.Duration < _config.MinimumIntroDuration)
        {
            return (new TimeRange(), new TimeRange());
        }

        // Since LHS had a contiguous time range, RHS must have one also.
        var rContiguous = TimeRangeHelpers.FindContiguous([.. rhsTimes], _config.MaximumTimeSkip)!;
        return (lContiguous, rContiguous);
    }

    /// <summary>
    /// Adjusts the end timestamps of all intros so that they end at silence.
    /// </summary>
    /// <param name="episode">QueuedEpisode to adjust.</param>
    /// <param name="originalIntro">Original introduction.</param>
    private Segment AdjustIntroTimes(
        QueuedEpisode episode,
        Segment originalIntro)
    {
        _logger.LogTrace(
            "{Name} original intro: {Start} - {End}",
            episode.Name,
            originalIntro.Start,
            originalIntro.End);

        var originalIntroStart = new TimeRange(
            Math.Max(0, (int)originalIntro.Start - 5),
            (int)originalIntro.Start + 10);

        var originalIntroEnd = new TimeRange(
            (int)originalIntro.End - 10,
            Math.Min(episode.Duration, (int)originalIntro.End + 5));

        // Try to adjust based on chapters first, fall back to silence detection for intros
        if (!AdjustIntroBasedOnChapters(episode, originalIntro, originalIntroStart, originalIntroEnd) &&
            _analysisMode == AnalysisMode.Introduction)
        {
            AdjustIntroBasedOnSilence(episode, originalIntro, originalIntroEnd);
        }

        _logger.LogTrace(
            "{Name} adjusted intro: {Start} - {End}",
            episode.Name,
            originalIntro.Start,
            originalIntro.End);

        return originalIntro;
    }

    private bool AdjustIntroBasedOnChapters(
        QueuedEpisode episode,
        Segment intro,
        TimeRange originalIntroStart,
        TimeRange originalIntroEnd)
    {
        var chapters = Plugin.Instance?.GetChapters(episode.EpisodeId) ?? [];
        double previousTime = 0;

        for (int i = 0; i <= chapters.Count; i++)
        {
            double currentTime = i < chapters.Count
                ? TimeSpan.FromTicks(chapters[i].StartPositionTicks).TotalSeconds
                : episode.Duration;

            if (IsTimeWithinRange(previousTime, originalIntroStart))
            {
                intro.Start = previousTime;
                _logger.LogTrace("{Name} chapter found close to intro start: {Start}", episode.Name, previousTime);
            }

            if (IsTimeWithinRange(currentTime, originalIntroEnd))
            {
                intro.End = currentTime;
                _logger.LogTrace("{Name} chapter found close to intro end: {End}", episode.Name, currentTime);
                return true;
            }

            previousTime = currentTime;
        }

        return false;
    }

    private void AdjustIntroBasedOnSilence(QueuedEpisode episode, Segment intro, TimeRange originalIntroEnd)
    {
        var silenceRanges = FFmpegWrapper.DetectSilence(episode, originalIntroEnd);

        foreach (var silenceRange in silenceRanges)
        {
            _logger.LogTrace("{Name} silence: {Start} - {End}", episode.Name, silenceRange.Start, silenceRange.End);

            if (IsValidSilenceForIntroAdjustment(silenceRange, originalIntroEnd, intro))
            {
                intro.End = silenceRange.Start;
                break;
            }
        }
    }

    private bool IsValidSilenceForIntroAdjustment(
        TimeRange silenceRange,
        TimeRange originalIntroEnd,
        Segment adjustedIntro)
    {
        return originalIntroEnd.Intersects(silenceRange) &&
               silenceRange.Duration >= _config.SilenceDetectionMinimumDuration &&
               silenceRange.Start >= adjustedIntro.Start;
    }

    private static bool IsTimeWithinRange(double time, TimeRange range)
    {
        return range.Start < time && time < range.End;
    }

    /// <summary>
    /// Transforms a Chromaprint into an inverted index of fingerprint points to the last index it appeared at.
    /// </summary>
    /// <param name="id">Episode ID.</param>
    /// <param name="fingerprint">Chromaprint fingerprint.</param>
    /// <returns>Inverted index.</returns>
    public Dictionary<uint, int> CreateInvertedIndex(Guid id, uint[] fingerprint)
    {
        if (_invertedIndexCache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var invIndex = new Dictionary<uint, int>();

        for (int i = 0; i < fingerprint.Length; i++)
        {
            // Get the current point.
            var point = fingerprint[i];

            // Append the current sample's timecode to the collection for this point.
            invIndex[point] = i;
        }

        _invertedIndexCache[id] = invIndex;

        return invIndex;
    }

    /// <summary>
    /// Count the number of bits that are set in the provided number.
    /// </summary>
    /// <param name="number">Number to count bits in.</param>
    /// <returns>Number of bits that are equal to 1.</returns>
    public static int CountBits(uint number)
    {
        return BitOperations.PopCount(number);
    }
}
