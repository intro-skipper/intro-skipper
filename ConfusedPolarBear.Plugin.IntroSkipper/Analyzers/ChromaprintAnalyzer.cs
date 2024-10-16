using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using ConfusedPolarBear.Plugin.IntroSkipper.Configuration;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Analyzers;

/// <summary>
/// Chromaprint audio analyzer.
/// </summary>
public class ChromaprintAnalyzer : IMediaFileAnalyzer
{
    /// <summary>
    /// Seconds of audio in one fingerprint point.
    /// This value is defined by the Chromaprint library and should not be changed.
    /// </summary>
    private const double SamplesToSeconds = 0.1238;

    private readonly int _minimumIntroDuration;

    private readonly int _maximumDifferences;

    private readonly int _invertedIndexShift;

    private readonly double _maximumTimeSkip;

    private readonly ILogger<ChromaprintAnalyzer> _logger;

    private AnalysisMode _analysisMode;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChromaprintAnalyzer"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public ChromaprintAnalyzer(ILogger<ChromaprintAnalyzer> logger)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        _maximumDifferences = config.MaximumFingerprintPointDifferences;
        _invertedIndexShift = config.InvertedIndexShift;
        _maximumTimeSkip = config.MaximumTimeSkip;
        _minimumIntroDuration = config.MinimumIntroDuration;

        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<QueuedEpisode> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        // All intros for this season.
        var seasonIntros = new Dictionary<Guid, Segment>();

        // Cache of all fingerprints for this season.
        var fingerprintCache = new Dictionary<Guid, uint[]>();

        // Episode analysis queue based on not analyzed episodes
        var episodeAnalysisQueue = new List<QueuedEpisode>(analysisQueue);

        // Episodes that were analyzed and do not have an introduction.
        var episodesWithoutIntros = episodeAnalysisQueue.Where(e => !e.State.IsAnalyzed(mode)).ToList();

        _analysisMode = mode;

        if (episodesWithoutIntros.Count == 0 || episodeAnalysisQueue.Count <= 1)
        {
            return analysisQueue;
        }

        var episodesWithFingerprint = new List<QueuedEpisode>(episodesWithoutIntros);

        // Load fingerprints from cache if available.
        episodesWithFingerprint.AddRange(episodeAnalysisQueue.Where(e => e.State.IsAnalyzed(mode) && File.Exists(FFmpegWrapper.GetFingerprintCachePath(e, mode))));

        // Ensure at least two fingerprints are present.
        if (episodesWithFingerprint.Count == 1)
        {
            var indexInAnalysisQueue = episodeAnalysisQueue.FindIndex(episode => episode == episodesWithoutIntros[0]);
            episodesWithFingerprint.AddRange(episodeAnalysisQueue
                .Where((episode, index) => Math.Abs(index - indexInAnalysisQueue) <= 1 && index != indexInAnalysisQueue));
        }

        seasonIntros = episodesWithFingerprint.Where(e => e.State.IsAnalyzed(mode)).ToDictionary(e => e.EpisodeId, e => Plugin.GetIntroByMode(e.EpisodeId, mode));

        // Compute fingerprints for all episodes in the season
        foreach (var episode in episodesWithFingerprint)
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
        while (episodesWithoutIntros.Count > 0)
        {
            // Pop the first episode from the queue
            var currentEpisode = episodesWithoutIntros[0];
            episodesWithoutIntros.RemoveAt(0);
            episodesWithFingerprint.Remove(currentEpisode);

            // Search through all remaining episodes.
            foreach (var remainingEpisode in episodesWithFingerprint)
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

            // If no intro is found at this point, the popped episode is not reinserted into the queue.
            if (seasonIntros.ContainsKey(currentEpisode.EpisodeId))
            {
                episodesWithFingerprint.Add(currentEpisode);
                episodeAnalysisQueue.FirstOrDefault(x => x.EpisodeId == currentEpisode.EpisodeId)?.State.SetAnalyzed(mode, true);
            }
        }

        // If cancellation was requested, report that no episodes were analyzed.
        if (cancellationToken.IsCancellationRequested)
        {
            return analysisQueue;
        }

        // Adjust all introduction times.
        var analyzerHelper = new AnalyzerHelper(_logger);
        seasonIntros = analyzerHelper.AdjustIntroTimes(analysisQueue, seasonIntros, _analysisMode);

        Plugin.Instance!.UpdateTimestamps(seasonIntros, _analysisMode);

        return episodeAnalysisQueue;
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
        var lhsIndex = FFmpegWrapper.CreateInvertedIndex(lhsId, lhsPoints, _analysisMode);
        var rhsIndex = FFmpegWrapper.CreateInvertedIndex(rhsId, rhsPoints, _analysisMode);
        var indexShifts = new HashSet<int>();

        // For all audio points in the left episode, check if the right episode has a point which matches exactly.
        // If an exact match is found, calculate the shift that must be used to align the points.
        foreach (var kvp in lhsIndex)
        {
            var originalPoint = kvp.Key;

            for (var i = -1 * _invertedIndexShift; i <= _invertedIndexShift; i++)
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
            if (CountBits(diff) > _maximumDifferences)
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
        var lContiguous = TimeRangeHelpers.FindContiguous(lhsTimes.ToArray(), _maximumTimeSkip);
        if (lContiguous is null || lContiguous.Duration < _minimumIntroDuration)
        {
            return (new TimeRange(), new TimeRange());
        }

        // Since LHS had a contiguous time range, RHS must have one also.
        var rContiguous = TimeRangeHelpers.FindContiguous(rhsTimes.ToArray(), _maximumTimeSkip)!;
        return (lContiguous, rContiguous);
    }

    /// <summary>
    /// Count the number of bits that are set in the provided number.
    /// </summary>
    /// <param name="number">Number to count bits in.</param>
    /// <returns>Number of bits that are equal to 1.</returns>
    public int CountBits(uint number)
    {
        return BitOperations.PopCount(number);
    }
}
