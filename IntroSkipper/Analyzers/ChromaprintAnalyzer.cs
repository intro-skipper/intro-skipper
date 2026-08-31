// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Numerics;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Initializes a new instance of the <see cref="ChromaprintAnalyzer"/> class.
/// </summary>
/// <param name="logger">Logger.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
public partial class ChromaprintAnalyzer(ILogger<ChromaprintAnalyzer> logger, IFFmpegService ffmpegService, IDetectionCacheService cacheService) : IMediaFileAnalyzer
{
    /// <summary>
    /// Minimum duration (seconds) for a shared recap card/sting to count as a candidate.
    /// </summary>
    private const double RecapCardMinimumDuration = 3.0;

    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<ChromaprintAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IDetectionCacheService _cacheService = cacheService;
    private readonly Dictionary<Guid, Dictionary<uint, int>> _invertedIndexCache = [];
    private readonly Dictionary<Guid, IReadOnlyList<BlackFrame>> _recapBlackFrameCache = [];
    private AnalysisMode _analysisMode;

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        // Episodes that need analysis (not yet analyzed or not user-provided) plus already-analyzed
        // episodes that still have a fingerprint cache and can be re-analyzed.
        var episodeAnalysisQueue = analysisQueue.Where(e =>
            e.NeedsAnalysis(mode) ||
            (e.GetAnalyzed(mode) == EpisodeState.Analyzed && _cacheService.HasCachedFingerprint(e, mode))).ToList();

        if (analysisQueue.Count <= 1 || episodeAnalysisQueue.All(e => e.GetAnalyzed(mode) == EpisodeState.Analyzed))
        {
            return analysisQueue;
        }

        _analysisMode = mode;

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config, mode, _ffmpegService);

        // All intros for this season.
        var seasonIntros = new Dictionary<Guid, Segment>();

        // Cache of all fingerprints for this season.
        var fingerprintCache = new Dictionary<Guid, uint[]>();

        // Ensure at least two fingerprints are present.
        if (episodeAnalysisQueue.Count == 1)
        {
            var currentEpisode = episodeAnalysisQueue[0];
            episodeAnalysisQueue.AddRange(analysisQueue
                .Where(episode => episode != currentEpisode && Math.Abs(episode.EpisodeNumber - currentEpisode.EpisodeNumber) <= 1));
        }

        // Compute fingerprints for all episodes in the season
        foreach (var episode in episodeAnalysisQueue)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                fingerprintCache[episode.EpisodeId] = await _ffmpegService.FingerprintAsync(episode, mode, cancellationToken).ConfigureAwait(false);
            }
            catch (FingerprintException ex)
            {
                LogCaughtFingerprintError(ex);
                WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);

                // Fallback to an empty fingerprint on any error, and record the failure so the episode
                // is retried on the next run. Without this it is persisted as a completed no-segment
                // result, and an ffmpeg hiccup on one episode becomes a permanent verdict.
                // Episodes that already carry a completed result (re-analysis of an Analyzed episode
                // with a cached fingerprint, or an Analyzed/UserProvided neighbor pulled in to pair
                // with a lone unanalyzed episode) keep that result: a failed fingerprint must not
                // discard a valid verdict.
                fingerprintCache[episode.EpisodeId] = [];
                if (episode.NeedsAnalysis(mode))
                {
                    episode.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
                }
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

                var maxDuration = GetMaximumSegmentDuration(remainingEpisode);

                // Ignore this comparison result if:
                // - one of the intros isn't valid, or
                // - the introduction exceeds the configured limit
                if (
                    !remainingIntro.Valid ||
                    remainingIntro.Duration > maxDuration)
                {
                    continue;
                }

                if (_analysisMode == AnalysisMode.Recap)
                {
                    currentIntro = await BuildRecapFromChromaprintCandidateAsync(
                        currentEpisode,
                        currentIntro,
                        cancellationToken).ConfigureAwait(false) ?? new Segment(currentEpisode.EpisodeId);
                    remainingIntro = await BuildRecapFromChromaprintCandidateAsync(
                        remainingEpisode,
                        remainingIntro,
                        cancellationToken).ConfigureAwait(false) ?? new Segment(remainingEpisode.EpisodeId);

                    if (!currentIntro.Valid || !remainingIntro.Valid ||
                        currentIntro.Duration > maxDuration ||
                        remainingIntro.Duration > maxDuration)
                    {
                        continue;
                    }
                }

                /* Since the FingerprintAsync() function returns an array of Chromaprint points without time
                 * information, the times reported from the index search function start from 0.
                 *
                 * While this is desired behavior for detecting introductions, it breaks credit
                 * detection, as the audio we're analyzing was extracted from some point into the file.
                 *
                 * To fix this, add the starting time of the fingerprint to the reported time range.
                 */
                if (_analysisMode == AnalysisMode.Credits)
                {
                    currentIntro.Start += currentEpisode.CreditsFingerprintStart;
                    currentIntro.End += currentEpisode.CreditsFingerprintStart;
                    remainingIntro.Start += remainingEpisode.CreditsFingerprintStart;
                    remainingIntro.End += remainingEpisode.CreditsFingerprintStart;
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
            if (seasonIntros.TryGetValue(currentEpisode.EpisodeId, out var intro))
            {
                var adjustedIntro = await timeAdjustmentHelper.AdjustIntroTimesAsync(currentEpisode, intro, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!adjustedIntro.Valid)
                {
                    await Plugin.DeleteAutomaticTimestampAsync(currentEpisode.EpisodeId, mode, cancellationToken).ConfigureAwait(false);
                    currentEpisode.SetAnalyzed(mode, EpisodeState.NoSegments);
                    continue;
                }

                currentEpisode.SetAnalyzed(mode, EpisodeState.Analyzed);
                await Plugin.Instance!.UpdateTimestampAsync(adjustedIntro, mode, configHash: currentEpisode.AnalysisConfigHash, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
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
            LogIndexSearchSuccessful();

            return SelectSharedRegion(lhsId, lhsRanges, rhsId, rhsRanges, _analysisMode);
        }

        LogSharedIntroNotFound(lhsId, rhsId);

        return (new Segment(lhsId), new Segment(rhsId));
    }

    /// <summary>
    /// Returns the minimum shared-region duration for the given analysis mode.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="minimumIntroDuration">Configured minimum intro duration.</param>
    /// <returns>Minimum region duration in seconds.</returns>
    internal static double GetMinimumRegionDuration(AnalysisMode mode, int minimumIntroDuration)
        => mode == AnalysisMode.Recap ? RecapCardMinimumDuration : minimumIntroDuration;

    /// <summary>
    /// Selects which shared audio region should be returned for the given analysis mode.
    /// Recap uses the earliest qualifying shared card/sting; other modes use the longest region.
    /// </summary>
    /// <param name="lhsId">First episode id.</param>
    /// <param name="lhsRanges">First episode shared timecodes.</param>
    /// <param name="rhsId">Second episode id.</param>
    /// <param name="rhsRanges">Second episode shared timecodes.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Segments for the first and second episodes.</returns>
    internal static (Segment Lhs, Segment Rhs) SelectSharedRegion(
        Guid lhsId,
        List<TimeRange> lhsRanges,
        Guid rhsId,
        List<TimeRange> rhsRanges,
        AnalysisMode mode)
        => mode == AnalysisMode.Recap
            ? GetEarliestTimeRange(lhsId, lhsRanges, rhsId, rhsRanges)
            : GetLongestTimeRange(lhsId, lhsRanges, rhsId, rhsRanges);

    private int GetMaximumSegmentDuration(QueuedEpisode episode)
    {
        return _analysisMode switch
        {
            AnalysisMode.Introduction => _config.MaximumIntroDuration,
            AnalysisMode.Recap => _config.MaximumRecapDetectionDuration,
            AnalysisMode.Credits => (int)(episode.Duration - episode.CreditsFingerprintStart - 1), // dont allow perfect matches to avoid false positives from duplicates
            _ => (int)episode.Duration
        };
    }

    private async Task<Segment?> BuildRecapFromChromaprintCandidateAsync(
        QueuedEpisode episode,
        Segment card,
        CancellationToken cancellationToken)
    {
        if (!card.Valid)
        {
            return null;
        }

        var maximumBoundary = await RecapDetectionHelper.GetMaximumBoundaryAsync(
            episode,
            _config,
            cancellationToken).ConfigureAwait(false);
        if (maximumBoundary <= card.End)
        {
            return null;
        }

        if (!_recapBlackFrameCache.TryGetValue(episode.EpisodeId, out var blackFrames))
        {
            // Share the adaptive scan with ChapterAnalyzer so both recap consumers agree on
            // which frames count as black instead of diverging by analyzer order.
            blackFrames = await RecapDetectionHelper.DetectAdaptiveBlackFramesAsync(
                _ffmpegService,
                episode,
                maximumBoundary,
                _config,
                cancellationToken).ConfigureAwait(false);
            _recapBlackFrameCache[episode.EpisodeId] = blackFrames;
        }

        return ChapterAnalyzer.BuildRecapFromBlackFrames(
            episode.EpisodeId,
            blackFrames,
            Math.Max(_config.MinimumRecapDetectionDuration, (int)Math.Ceiling(card.End)),
            maximumBoundary);
    }

    private static (Segment Lhs, Segment Rhs) GetEarliestTimeRange(
        Guid lhsId,
        List<TimeRange> lhsRanges,
        Guid rhsId,
        List<TimeRange> rhsRanges)
    {
        var pairCount = Math.Min(lhsRanges.Count, rhsRanges.Count);
        if (pairCount == 0)
        {
            return (new Segment(lhsId), new Segment(rhsId));
        }

        var earliestIndex = 0;
        for (var i = 1; i < pairCount; i++)
        {
            if (lhsRanges[i].Start < lhsRanges[earliestIndex].Start)
            {
                earliestIndex = i;
            }
        }

        var lhsRecap = new TimeRange(lhsRanges[earliestIndex]);
        var rhsRecap = new TimeRange(rhsRanges[earliestIndex]);

        if (lhsRecap.Start <= 5)
        {
            lhsRecap.Start = 0;
        }

        if (rhsRecap.Start <= 5)
        {
            rhsRecap.Start = 0;
        }

        return (new Segment(lhsId, lhsRecap), new Segment(rhsId, rhsRecap));
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
        if (lhsRanges.Count == 0 || rhsRanges.Count == 0)
        {
            return (new Segment(lhsId), new Segment(rhsId));
        }

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

            var lhsTime = lhsPosition * ChromaprintConstants.SampleDuration;
            var rhsTime = rhsPosition * ChromaprintConstants.SampleDuration;

            lhsTimes.Add(lhsTime);
            rhsTimes.Add(rhsTime);
        }

        // Now that both fingerprints have been compared at this shift, see if there's a contiguous time range.
        var lContiguous = TimeRangeHelpers.FindContiguous([.. lhsTimes], _config.MaximumTimeSkip);
        if (lContiguous is null || lContiguous.Duration < GetMinimumRegionDuration(_analysisMode, _config.MinimumIntroDuration))
        {
            return (new TimeRange(), new TimeRange());
        }

        // Since LHS had a contiguous time range, RHS must have one also.
        var rContiguous = TimeRangeHelpers.FindContiguous([.. rhsTimes], _config.MaximumTimeSkip)!;
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Caught fingerprint error")]
    private partial void LogCaughtFingerprintError(Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Index search successful")]
    private partial void LogIndexSearchSuccessful();

    [LoggerMessage(Level = LogLevel.Trace, Message = "Unable to find a shared introduction sequence between {LHS} and {RHS}")]
    private partial void LogSharedIntroNotFound(Guid lhs, Guid rhs);
}
