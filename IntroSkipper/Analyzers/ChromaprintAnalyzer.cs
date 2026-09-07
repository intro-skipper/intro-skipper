// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Numerics;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Initializes a new instance of the <see cref="ChromaprintAnalyzer"/> class.
/// </summary>
/// <param name="logger">Logger.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="configuration">Plugin configuration, or <see langword="null"/> to use the active plugin configuration.</param>
internal sealed partial class ChromaprintAnalyzer(
    ILogger<ChromaprintAnalyzer> logger,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    IIntroSkipperDatabase database,
    PluginConfiguration? configuration = null) : IMediaFileAnalyzer
{
    /// <summary>
    /// Minimum duration (seconds) for a shared recap card/sting to count as a candidate.
    /// </summary>
    private const double RecapCardMinimumDuration = 3.0;

    private readonly PluginConfiguration _config = configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<ChromaprintAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly Dictionary<Guid, Dictionary<uint, int>> _invertedIndexCache = [];

    // Per-episode recap inputs, reused across every pair the episode takes part in: the
    // boundary is a segment read and the frames an ffmpeg scan, both inside the O(n^2) loop.
    private readonly Dictionary<Guid, double> _recapBoundaryCache = [];
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

                // Keep a transient fingerprint failure retriable. A completed neighbor may be
                // included only to provide a comparison fingerprint; do not discard its valid result.
                fingerprintCache[episode.EpisodeId] = [];
                if (episode.NeedsAnalysis(mode))
                {
                    episode.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
                }
            }
        }

        for (var current = 0; current < episodeAnalysisQueue.Count; current++)
        {
            var currentEpisode = episodeAnalysisQueue[current];

            // Search through all remaining episodes.
            for (var remaining = current + 1; remaining < episodeAnalysisQueue.Count; remaining++)
            {
                var remainingEpisode = episodeAnalysisQueue[remaining];

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

                // Only save the discovered intro if it is the first one for this episode or
                // beats the saved one (see IsBetterCandidate).
                if (
                    !seasonIntros.TryGetValue(currentIntro.EpisodeId, out var savedCurrentIntro) ||
                    IsBetterCandidate(currentIntro, savedCurrentIntro, _analysisMode, _config.AnchorRecapToColdOpen, _config.EndSnapThreshold))
                {
                    seasonIntros[currentIntro.EpisodeId] = currentIntro;
                }

                if (
                    !seasonIntros.TryGetValue(remainingIntro.EpisodeId, out var savedRemainingIntro) ||
                    IsBetterCandidate(remainingIntro, savedRemainingIntro, _analysisMode, _config.AnchorRecapToColdOpen, _config.EndSnapThreshold))
                {
                    seasonIntros[remainingIntro.EpisodeId] = remainingIntro;
                }

                // One shared region settles most modes. An anchored recap keeps comparing: the
                // first pair may have matched an opening logo at 0:00 while a later pair holds
                // the sting after the cold open, which IsBetterCandidate prefers.
                if (_analysisMode != AnalysisMode.Recap || !_config.AnchorRecapToColdOpen)
                {
                    break;
                }
            }

            // If an intro is found for this episode, adjust its times and save it else add it to the list of episodes without intros.
            if (seasonIntros.TryGetValue(currentEpisode.EpisodeId, out var intro))
            {
                var adjustedIntro = await timeAdjustmentHelper.AdjustIntroTimesAsync(currentEpisode, intro, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!adjustedIntro.Valid)
                {
                    await _database.ReplaceAutoSegmentsAsync(currentEpisode.EpisodeId, mode, [], SegmentSource.Chromaprint, currentEpisode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
                    currentEpisode.SetAnalyzed(mode, EpisodeState.NoSegments);
                    continue;
                }

                currentEpisode.SetAnalyzed(mode, EpisodeState.Analyzed);
                await _database.ReplaceAutoSegmentsAsync(currentEpisode.EpisodeId, mode, [adjustedIntro], SegmentSource.Chromaprint, currentEpisode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
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
    internal (Segment Lhs, Segment Rhs) CompareEpisodes(
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
    /// A region starting within 5 s of the episode start is snapped to 0.
    /// </summary>
    /// <param name="lhsId">First episode id.</param>
    /// <param name="lhsRanges">First episode shared timecodes.</param>
    /// <param name="rhsId">Second episode id.</param>
    /// <param name="rhsRanges">Second episode shared timecodes, index-aligned with <paramref name="lhsRanges"/>.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>Segments for the first and second episodes; invalid segments when there is no pair.</returns>
    internal static (Segment Lhs, Segment Rhs) SelectSharedRegion(
        Guid lhsId,
        List<TimeRange> lhsRanges,
        Guid rhsId,
        List<TimeRange> rhsRanges,
        AnalysisMode mode)
    {
        var pairCount = Math.Min(lhsRanges.Count, rhsRanges.Count);
        if (pairCount == 0)
        {
            return (new Segment(lhsId), new Segment(rhsId));
        }

        // The lists are index-aligned pairs from the same shift, so the pair is picked by the
        // left-hand range alone.
        var selected = 0;
        for (var i = 1; i < pairCount; i++)
        {
            var better = mode == AnalysisMode.Recap
                ? lhsRanges[i].Start < lhsRanges[selected].Start
                : lhsRanges[i].Duration > lhsRanges[selected].Duration;
            if (better)
            {
                selected = i;
            }
        }

        var lhs = new TimeRange(lhsRanges[selected]);
        var rhs = new TimeRange(rhsRanges[selected]);
        if (lhs.Start <= 5)
        {
            lhs.Start = 0;
        }

        if (rhs.Start <= 5)
        {
            rhs.Start = 0;
        }

        return (new Segment(lhsId, lhs), new Segment(rhsId, rhs));
    }

    /// <summary>
    /// Decides whether a candidate found in a later episode pair replaces the one already saved
    /// for the same episode. Longer wins. The exception is an anchored recap: recap candidates
    /// for one episode share their end (the last black frame before the boundary), so a start
    /// beyond the start snap threshold means a sting past the cold open was found, and it beats
    /// a candidate that snaps to 0:00 even though that one is longer.
    /// </summary>
    /// <param name="candidate">Newly found segment.</param>
    /// <param name="saved">Segment already saved for the same episode.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="anchorRecapToColdOpen">Whether recaps are anchored to the cold open.</param>
    /// <param name="endSnapThreshold">Configured episode boundary snap threshold in seconds.</param>
    /// <returns><see langword="true"/> when <paramref name="candidate"/> should replace <paramref name="saved"/>.</returns>
    internal static bool IsBetterCandidate(Segment candidate, Segment saved, AnalysisMode mode, bool anchorRecapToColdOpen, double endSnapThreshold)
    {
        if (mode == AnalysisMode.Recap && anchorRecapToColdOpen)
        {
            var candidateSnaps = TimeAdjustmentHelper.IsWithinStartSnapThreshold(candidate.Start, endSnapThreshold);
            var savedSnaps = TimeAdjustmentHelper.IsWithinStartSnapThreshold(saved.Start, endSnapThreshold);
            if (candidateSnaps != savedSnaps)
            {
                return !candidateSnaps;
            }
        }

        return candidate.Duration > saved.Duration;
    }

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

        if (!_recapBoundaryCache.TryGetValue(episode.EpisodeId, out var maximumBoundary))
        {
            maximumBoundary = await RecapDetectionHelper.GetMaximumBoundaryAsync(
                _database,
                episode,
                _config,
                cancellationToken).ConfigureAwait(false);
            _recapBoundaryCache[episode.EpisodeId] = maximumBoundary;
        }

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

        return RecapDetectionHelper.BuildRecapFromSting(
            episode.EpisodeId,
            card,
            blackFrames,
            _config.MinimumRecapDetectionDuration,
            maximumBoundary,
            _config.AnchorRecapToColdOpen);
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
        foreach (var (originalPoint, lhsPosition) in lhsIndex)
        {
            for (var i = -1 * _config.InvertedIndexShift; i <= _config.InvertedIndexShift; i++)
            {
                if (rhsIndex.TryGetValue((uint)(originalPoint + i), out var rhsPosition))
                {
                    indexShifts.Add(rhsPosition - lhsPosition);
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
    /// <remarks>
    /// A run-length pass over the sample positions: a run continues while the gap between two
    /// similar samples is at most <see cref="PluginConfiguration.MaximumTimeSkip"/>; the longest
    /// run wins, the earlier one on a tie.
    /// </remarks>
    /// <param name="lhs">First fingerprint to compare.</param>
    /// <param name="rhs">Second fingerprint to compare.</param>
    /// <param name="shiftAmount">Amount to shift one fingerprint by.</param>
    /// <returns>The shared range in each fingerprint, or two empty ranges when none is long enough.</returns>
    private (TimeRange Lhs, TimeRange Rhs) FindContiguous(
        uint[] lhs,
        uint[] rhs,
        int shiftAmount)
    {
        var leftOffset = shiftAmount < 0 ? -shiftAmount : 0;
        var rightOffset = shiftAmount > 0 ? shiftAmount : 0;
        var upperLimit = Math.Min(lhs.Length, rhs.Length) - Math.Abs(shiftAmount);

        // Sample positions on the LHS; -1 while no run has started.
        var bestStart = -1;
        var bestEnd = -1;
        var runStart = -1;
        var runEnd = -1;

        for (var i = 0; i < upperLimit; i++)
        {
            var lhsPosition = i + leftOffset;
            if (BitOperations.PopCount(lhs[lhsPosition] ^ rhs[i + rightOffset]) > _config.MaximumFingerprintPointDifferences)
            {
                continue;
            }

            if (runStart >= 0 && (lhsPosition - runEnd) * ChromaprintConstants.SampleDuration <= _config.MaximumTimeSkip)
            {
                runEnd = lhsPosition;
                continue;
            }

            if (runStart >= 0 && (bestStart < 0 || runEnd - runStart > bestEnd - bestStart))
            {
                (bestStart, bestEnd) = (runStart, runEnd);
            }

            runStart = runEnd = lhsPosition;
        }

        if (runStart >= 0 && (bestStart < 0 || runEnd - runStart > bestEnd - bestStart))
        {
            (bestStart, bestEnd) = (runStart, runEnd);
        }

        if (bestStart < 0 || (bestEnd - bestStart) * ChromaprintConstants.SampleDuration < GetMinimumRegionDuration(_analysisMode, _config.MinimumIntroDuration))
        {
            return (new TimeRange(), new TimeRange());
        }

        // Every RHS position is its LHS position plus the shift.
        var positionShift = rightOffset - leftOffset;
        return (
            new TimeRange(bestStart * ChromaprintConstants.SampleDuration, bestEnd * ChromaprintConstants.SampleDuration),
            new TimeRange((bestStart + positionShift) * ChromaprintConstants.SampleDuration, (bestEnd + positionShift) * ChromaprintConstants.SampleDuration));
    }

    /// <summary>
    /// Transforms a Chromaprint into an inverted index of fingerprint points to the last index it appeared at.
    /// </summary>
    /// <param name="id">Episode ID.</param>
    /// <param name="fingerprint">Chromaprint fingerprint.</param>
    /// <returns>Inverted index.</returns>
    internal Dictionary<uint, int> CreateInvertedIndex(Guid id, uint[] fingerprint)
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Caught fingerprint error")]
    private partial void LogCaughtFingerprintError(Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Unable to find a shared introduction sequence between {LHS} and {RHS}")]
    private partial void LogSharedIntroNotFound(Guid lhs, Guid rhs);
}
