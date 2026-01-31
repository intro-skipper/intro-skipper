// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
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
    public Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        ICollection<AnalyzedSegment> analyzedSegments,
        CancellationToken cancellationToken)
    {
        // Episodes that were not analyzed or have a fingerprint cache.
        var episodeAnalysisQueue = analysisQueue.Where(e => !e.GetAnalyzed(mode) || File.Exists(FFmpegWrapper.GetFingerprintCachePath(e, mode))).ToList();

        if (analysisQueue.Count <= 1 || episodeAnalysisQueue.All(e => e.GetAnalyzed(mode)))
        {
            return Task.FromResult(analysisQueue);
        }

        _analysisMode = mode;

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config);

        // All segments for this season, keyed by episode ID.
        // Each episode can have multiple segments.
        var seasonSegments = new Dictionary<Guid, List<Segment>>();

        // Track matched episodes using Union-Find to determine first appearances.
        var episodeCluster = new EpisodeCluster();

        // Cache of all fingerprints for this season.
        var fingerprintCache = new Dictionary<Guid, uint[]>();

        // Create a lookup from episode ID to QueuedEpisode for later use.
        var episodeLookup = analysisQueue.ToDictionary(e => e.EpisodeId);

        // Ensure at least two fingerprints are present.
        if (episodeAnalysisQueue.Count == 1)
        {
            var currentEpisode = episodeAnalysisQueue[0];
            episodeAnalysisQueue.AddRange(analysisQueue
                .Where(episode => episode != currentEpisode && Math.Abs(episode.EpisodeNumber - currentEpisode.EpisodeNumber) <= 1));
        }

        // Compute fingerprints for all episodes in the season and register them in the cluster
        foreach (var episode in episodeAnalysisQueue)
        {
            try
            {
                fingerprintCache[episode.EpisodeId] = FFmpegWrapper.Fingerprint(episode, mode);
                episodeCluster.Register(episode.EpisodeId, episode.EpisodeNumber);

                // Use reversed fingerprints for credits
                if (_analysisMode == AnalysisMode.Credits)
                {
                    Array.Reverse(fingerprintCache[episode.EpisodeId]);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromResult(analysisQueue);
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
                var (currentSegments, remainingSegments) = CompareEpisodes(
                    currentEpisode.EpisodeId,
                    fingerprintCache[currentEpisode.EpisodeId],
                    remainingEpisode.EpisodeId,
                    fingerprintCache[remainingEpisode.EpisodeId]);

                var maxDuration = _analysisMode == AnalysisMode.Introduction
                    ? Plugin.Instance!.Configuration.MaximumIntroDuration
                    : (int)(remainingEpisode.Duration - remainingEpisode.CreditsFingerprintStart - 1); // dont allow perfect matches to avoid false positives from duplicates

                // Filter segments by maximum duration
                var validCurrentSegments = currentSegments.Where(s => s.Duration <= maxDuration).ToList();
                var validRemainingSegments = remainingSegments.Where(s => s.Duration <= maxDuration).ToList();

                if (validRemainingSegments.Count == 0)
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
                    foreach (var segment in validCurrentSegments)
                    {
                        double originalStart = segment.Start;
                        segment.Start = currentEpisode.Duration - segment.End;
                        segment.End = currentEpisode.Duration - originalStart;
                    }

                    foreach (var segment in validRemainingSegments)
                    {
                        double originalStart = segment.Start;
                        segment.Start = remainingEpisode.Duration - segment.End;
                        segment.End = remainingEpisode.Duration - originalStart;
                    }
                }

                // Merge new segments with existing ones for each episode
                MergeSegments(seasonSegments, currentEpisode.EpisodeId, validCurrentSegments);
                MergeSegments(seasonSegments, remainingEpisode.EpisodeId, validRemainingSegments);

                // Union the two matched episodes into the same cluster.
                // The cluster tracks which episode has the lowest episode number.
                episodeCluster.Union(currentEpisode.EpisodeId, remainingEpisode.EpisodeId);

                break;
            }

            // If segments are found for this episode, adjust their times and add to output collection.
            if (seasonSegments.TryGetValue(currentEpisode.EpisodeId, out var segments) && segments.Count > 0)
            {
                var isFirstAppearance = episodeCluster.IsFirstAppearance(currentEpisode.EpisodeId);

                foreach (var segment in segments)
                {
                    var adjustedSegment = timeAdjustmentHelper.AdjustIntroTimes(currentEpisode, segment);
                    analyzedSegments.Add(new AnalyzedSegment(adjustedSegment, isFirstAppearance));
                }

                currentEpisode.SetAnalyzed(mode, true);
            }
        }

        return Task.FromResult(analysisQueue);
    }

    /// <summary>
    /// Merges new segments into the existing segment collection for an episode.
    /// Keeps the longer segment when overlaps occur.
    /// </summary>
    private static void MergeSegments(Dictionary<Guid, List<Segment>> seasonSegments, Guid episodeId, List<Segment> newSegments)
    {
        if (!seasonSegments.TryGetValue(episodeId, out var existingSegments))
        {
            existingSegments = [];
            seasonSegments[episodeId] = existingSegments;
        }

        foreach (var newSegment in newSegments)
        {
            // Check if this segment overlaps with any existing segment
            var overlappingIndex = existingSegments.FindIndex(s =>
                (newSegment.Start >= s.Start && newSegment.Start <= s.End) ||
                (newSegment.End >= s.Start && newSegment.End <= s.End) ||
                (newSegment.Start <= s.Start && newSegment.End >= s.End));

            if (overlappingIndex >= 0)
            {
                // Keep the longer segment
                if (newSegment.Duration > existingSegments[overlappingIndex].Duration)
                {
                    existingSegments[overlappingIndex] = newSegment;
                }
            }
            else
            {
                // No overlap, add as new segment
                existingSegments.Add(newSegment);
            }
        }
    }

    /// <summary>
    /// Analyze two episodes to find introduction sequences shared between them.
    /// </summary>
    /// <param name="lhsId">First episode id.</param>
    /// <param name="lhsPoints">First episode fingerprint points.</param>
    /// <param name="rhsId">Second episode id.</param>
    /// <param name="rhsPoints">Second episode fingerprint points.</param>
    /// <returns>Lists of segments for the first and second episodes.</returns>
    public (List<Segment> Lhs, List<Segment> Rhs) CompareEpisodes(
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

            return GetAllTimeRanges(lhsId, lhsRanges, rhsId, rhsRanges);
        }

        _logger.LogTrace(
            "Unable to find a shared introduction sequence between {LHS} and {RHS}",
            lhsId,
            rhsId);

        return ([], []);
    }

    /// <summary>
    /// Converts all significant time ranges to Segment objects.
    /// </summary>
    /// <param name="lhsId">First episode id.</param>
    /// <param name="lhsRanges">First episode shared timecodes.</param>
    /// <param name="rhsId">Second episode id.</param>
    /// <param name="rhsRanges">Second episode shared timecodes.</param>
    /// <returns>Lists of segments for the first and second episodes.</returns>
    private static (List<Segment> Lhs, List<Segment> Rhs) GetAllTimeRanges(
        Guid lhsId,
        List<TimeRange> lhsRanges,
        Guid rhsId,
        List<TimeRange> rhsRanges)
    {
        var lhsSegments = new List<Segment>();
        var rhsSegments = new List<Segment>();

        // Sort ranges by duration (longest first)
        lhsRanges.Sort();
        rhsRanges.Sort();

        // Convert all ranges to segments
        foreach (var range in lhsRanges)
        {
            var adjustedRange = new TimeRange(range.Start, range.End);

            // If the intro starts early in the episode, move it to the beginning.
            if (adjustedRange.Start <= 5)
            {
                adjustedRange.Start = 0;
            }

            lhsSegments.Add(new Segment(lhsId, adjustedRange));
        }

        foreach (var range in rhsRanges)
        {
            var adjustedRange = new TimeRange(range.Start, range.End);

            // If the intro starts early in the episode, move it to the beginning.
            if (adjustedRange.Start <= 5)
            {
                adjustedRange.Start = 0;
            }

            rhsSegments.Add(new Segment(rhsId, adjustedRange));
        }

        return (lhsSegments, rhsSegments);
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
            var (lhsContiguousRanges, rhsContiguousRanges) = FindAllContiguous(lhsPoints, rhsPoints, shift);

            lhsRanges.AddRange(lhsContiguousRanges);
            rhsRanges.AddRange(rhsContiguousRanges);
        }

        return (lhsRanges, rhsRanges);
    }

    /// <summary>
    /// Finds all contiguous regions of similar audio between two fingerprints using the provided shift amount.
    /// </summary>
    /// <param name="lhs">First fingerprint to compare.</param>
    /// <param name="rhs">Second fingerprint to compare.</param>
    /// <param name="shiftAmount">Amount to shift one fingerprint by.</param>
    private (List<TimeRange> Lhs, List<TimeRange> Rhs) FindAllContiguous(
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

        // Find all contiguous time ranges that meet the minimum duration
        var lContiguous = TimeRangeHelpers.FindAllContiguous([.. lhsTimes], _config.MaximumTimeSkip, _config.MinimumIntroDuration);
        var rContiguous = TimeRangeHelpers.FindAllContiguous([.. rhsTimes], _config.MaximumTimeSkip, _config.MinimumIntroDuration);

        return (lContiguous, rContiguous);
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
