// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
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
/// <param name="applyLocalConsensus">Whether to require local group consensus for a settled-season re-analysis.</param>
internal sealed partial class ChromaprintAnalyzer(
    ILogger<ChromaprintAnalyzer> logger,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    IntroSkipperDatabase database,
    PluginConfiguration? configuration = null,
    bool applyLocalConsensus = false) : IMediaFileAnalyzer
{
    /// <summary>
    /// Minimum duration (seconds) for a shared recap card/sting to count as a candidate. Shorter
    /// regions are mostly studio logos and network jingles shared by every episode.
    /// </summary>
    private const double RecapCardMinimumDuration = 4.0;

    // A recap sting must be shared with at least this many other episodes, and a sting every
    // episode of a season of at least RecapUbiquityMinimumEpisodes shares is a logo or an
    // opening theme, not a recap: the first episode almost never has one.
    private const int RecapMinimumSupport = 2;
    private const int RecapUbiquityMinimumEpisodes = 5;

    // Compare at most two successfully fingerprinted episodes away on either side. A local window
    // lets anime openings and endings change mid-season without one theme dominating the season.
    private const int ConsensusNeighborRadius = 2;
    private const double ConsensusMinimumRangeOverlap = 0.8;
    private const int ConsensusSupermajorityNumerator = 3;
    private const int ConsensusSupermajorityDenominator = 4;

    private readonly PluginConfiguration _config = configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILogger<ChromaprintAnalyzer> _logger = logger;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly IntroSkipperDatabase _database = database;
    private readonly bool _applyLocalConsensus = applyLocalConsensus;
    private readonly Dictionary<Guid, Dictionary<uint, int>> _invertedIndexCache = [];

    // Per-episode recap inputs, reused across every pair the episode takes part in: the
    // boundary is a segment read and the frames an ffmpeg scan, both inside the O(n^2) loop.
    private readonly Dictionary<Guid, double> _recapBoundaryCache = [];
    private readonly Dictionary<Guid, IReadOnlyList<BlackFrame>> _recapBlackFrameCache = [];
    private AnalysisMode _analysisMode;

    /// <inheritdoc />
    public Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
        => AnalyzeMediaFiles(analysisQueue, mode, [], cancellationToken);

    /// <summary>
    /// Analyzes the write targets while allowing other queued episodes to contribute cached or
    /// freshly extracted fingerprints to the season comparison without receiving result writes.
    /// </summary>
    /// <param name="analysisQueue">Episodes eligible for analysis and result writes.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="comparisonReferences">Additional episodes used only as fingerprint neighbors.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes still eligible for analysis.</returns>
    internal async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        IReadOnlyList<QueuedEpisode> comparisonReferences,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<QueuedEpisode> comparisonQueue = analysisQueue;
        if (comparisonReferences.Count > 0)
        {
            var combinedQueue = analysisQueue.ToList();
            var includedIds = analysisQueue.Select(episode => episode.EpisodeId).ToHashSet();
            combinedQueue.AddRange(comparisonReferences.Where(episode => includedIds.Add(episode.EpisodeId)));
            comparisonQueue = combinedQueue;
        }

        var (seasonIntros, fingerprintFailures) = await FindCandidatesAsync(comparisonQueue, mode, cancellationToken).ConfigureAwait(false);
        foreach (var episode in analysisQueue.Where(e => fingerprintFailures.Contains(e.EpisodeId)))
        {
            episode.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
        }

        if (seasonIntros.Count == 0)
        {
            return analysisQueue;
        }

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config, mode, _ffmpegService);

        foreach (var currentEpisode in analysisQueue)
        {
            // A user-provided neighbour is padded into the comparison only for its fingerprint.
            if (currentEpisode.GetAnalyzed(mode) == EpisodeState.UserProvided ||
                !seasonIntros.TryGetValue(currentEpisode.EpisodeId, out var intro))
            {
                continue;
            }

            // A settled sibling is re-derived only from a Chromaprint result; one another
            // analyzer settled keeps its rows and was compared only for its fingerprint.
            if (!currentEpisode.NeedsAnalysis(mode)
                && (await _database.GetSegmentsAsync(currentEpisode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false))
                    .Any(row => row.Type == mode && row.Source != SegmentSource.Chromaprint))
            {
                continue;
            }

            // Keyframe snapping can fail on every pass for one file, such as one whose tags push
            // the codec probe's stream mapping past its capture; that fails only this episode.
            Segment adjustedIntro;
            try
            {
                adjustedIntro = await timeAdjustmentHelper.AdjustIntroTimesAsync(currentEpisode, intro, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                currentEpisode.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
                LogErrorAdjustingIntro(ex, mode, currentEpisode.Name);
                continue;
            }

            if (!adjustedIntro.Valid)
            {
                await _database.ReplaceAutoSegmentsAsync(currentEpisode.EpisodeId, mode, [], SegmentSource.Chromaprint, currentEpisode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
                currentEpisode.SetAnalyzed(mode, EpisodeState.NoSegments);
                continue;
            }

            // A write a tombstone, a user segment or a higher-ranked current row refused leaves
            // the episode to the analyzers after this one, as a refused chapter write does.
            var written = await _database.ReplaceAutoSegmentsAsync(currentEpisode.EpisodeId, mode, [adjustedIntro], SegmentSource.Chromaprint, currentEpisode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
            if (written > 0)
            {
                currentEpisode.SetAnalyzed(mode, EpisodeState.Analyzed);
            }
        }

        return analysisQueue;
    }

    /// <summary>
    /// Finds the shared-audio candidate of every episode in the season without adjusting
    /// times or writing. The credits pass combines these with other analyzers' candidates
    /// for the episodes no credits chapter settled.
    /// </summary>
    /// <remarks>
    /// Compares the episodes that need analysis plus the already-analyzed ones that still
    /// have a cached fingerprint, so a newly added episode re-derives its siblings'
    /// candidates from the full season. Episode state is left to the caller: an episode whose
    /// fingerprint fails gets no candidate and, when it still needed analysis, is reported in
    /// the result's failures.
    /// </remarks>
    /// <param name="analysisQueue">The season's queued media files, analyzed or not.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw candidate per episode id, in file seconds, and the episodes whose fingerprint failed; both empty when the season has nothing to compare.</returns>
    internal async Task<ChromaprintCandidates> FindCandidatesAsync(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        // All intros for this season.
        var seasonIntros = new Dictionary<Guid, Segment>();
        var fingerprintFailures = new HashSet<Guid>();

        // Episodes that need analysis (not yet analyzed or not user-provided) plus already-analyzed
        // episodes that still have a fingerprint cache and can be re-analyzed.
        var episodeAnalysisQueue = analysisQueue.Where(e =>
            e.NeedsAnalysis(mode) ||
            (e.GetAnalyzed(mode) == EpisodeState.Analyzed && _cacheService.HasCachedFingerprint(e, mode))).ToList();

        if (analysisQueue.Count <= 1 || episodeAnalysisQueue.All(e => e.GetAnalyzed(mode) == EpisodeState.Analyzed))
        {
            return new ChromaprintCandidates(seasonIntros, fingerprintFailures);
        }

        _analysisMode = mode;

        // Per episode, the partners a valid recap candidate was found with (Recap only).
        var recapSupport = new Dictionary<Guid, HashSet<Guid>>();

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
                if (ex.InnerException is TimeoutException)
                {
                    LogFingerprintTimeout(ex, episode.Name, episode.Path);
                }
                else
                {
                    LogCaughtFingerprintError(ex, episode.Path);
                }

                WarningManager.RecordFingerprintFailure($"{episode.EpisodeId:N}: {ex.Message}");

                // Keep a transient fingerprint failure retriable. A completed neighbor may be
                // included only to provide a comparison fingerprint; do not discard its valid result.
                fingerprintCache[episode.EpisodeId] = [];
                if (episode.NeedsAnalysis(mode))
                {
                    fingerprintFailures.Add(episode.EpisodeId);
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
                // - the introduction exceeds the maximum for this mode
                if (
                    !remainingIntro.Valid ||
                    remainingIntro.Duration > maxDuration)
                {
                    // A valid shared region can still exceed the maximum; log it so an overlong
                    // opening is not mistaken for a failed match.
                    if (remainingIntro.Valid)
                    {
                        LogSharedRegionTooLong(
                            currentEpisode.EpisodeId,
                            remainingEpisode.EpisodeId,
                            _analysisMode,
                            remainingIntro.Duration,
                            maxDuration);
                    }

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

                    GetOrAdd(recapSupport, currentEpisode.EpisodeId).Add(remainingEpisode.EpisodeId);
                    GetOrAdd(recapSupport, remainingEpisode.EpisodeId).Add(currentEpisode.EpisodeId);
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

                // One shared region settles most modes. A recap keeps comparing: it counts how many
                // episodes share the sting, and an anchored one may find the sting after the cold
                // open in a later pair, which IsBetterCandidate prefers.
                if (_analysisMode != AnalysisMode.Recap)
                {
                    break;
                }
            }
        }

        if (_analysisMode == AnalysisMode.Recap)
        {
            seasonIntros = FilterRecapBySeasonSupport(seasonIntros, recapSupport, episodeAnalysisQueue.Count);
        }

        if (_applyLocalConsensus && (_analysisMode is AnalysisMode.Introduction or AnalysisMode.Credits))
        {
            seasonIntros = ConfirmLocalConsensus(episodeAnalysisQueue, fingerprintCache, seasonIntros, mode, cancellationToken);
        }

        return new ChromaprintCandidates(seasonIntros, fingerprintFailures);
    }

    /// <summary>
    /// Drops recap candidates that are not a recap sting. A sting shared with fewer than
    /// <see cref="RecapMinimumSupport"/> other episodes is a chance match, and one shared by every
    /// episode of a season of at least <see cref="RecapUbiquityMinimumEpisodes"/> is a studio logo
    /// or an opening theme. A season too small for the support rule asks for one partner per
    /// other episode at most. Changing the rule needs a new recap sting token in <c>ConfigHasher</c>.
    /// </summary>
    /// <param name="candidates">Recap candidates per episode.</param>
    /// <param name="support">Per episode, the partners a valid recap was found with.</param>
    /// <param name="comparedEpisodes">Number of episodes that took part in the comparison.</param>
    /// <returns>The candidates that survive.</returns>
    internal static Dictionary<Guid, Segment> FilterRecapBySeasonSupport(
        IReadOnlyDictionary<Guid, Segment> candidates,
        IReadOnlyDictionary<Guid, HashSet<Guid>> support,
        int comparedEpisodes)
    {
        var required = Math.Min(RecapMinimumSupport, Math.Max(1, comparedEpisodes - 1));
        var supported = candidates
            .Where(pair => support.TryGetValue(pair.Key, out var partners) && partners.Count >= required)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (comparedEpisodes >= RecapUbiquityMinimumEpisodes && supported.Count >= comparedEpisodes)
        {
            return [];
        }

        return supported;
    }

    // The partner set of a key, created on first use.
    internal static HashSet<Guid> GetOrAdd(Dictionary<Guid, HashSet<Guid>> map, Guid key)
    {
        if (!map.TryGetValue(key, out var set))
        {
            map[key] = set = [];
        }

        return set;
    }

    /// <summary>
    /// Keeps season-selected Introduction and Credits ranges only when each forms a local
    /// connected group across the configured number of distinct episodes, capped at the number
    /// of usable fingerprints. Comparable pattern groups may lower that target to their smallest
    /// group size so a mid-season theme change does not invalidate both patterns. This is used
    /// during settled-season re-analysis; each pair is limited to two successfully fingerprinted
    /// positions.
    /// </summary>
    /// <param name="episodes">Episodes whose fingerprints took part in the season comparison.</param>
    /// <param name="fingerprints">The fingerprints already extracted for those episodes.</param>
    /// <param name="candidates">Candidates selected by the existing season comparison.</param>
    /// <param name="mode">Analysis mode whose duration limits and time offsets apply.</param>
    /// <param name="cancellationToken">Token used to stop pair comparisons.</param>
    /// <returns>Locally confirmed candidates.</returns>
    internal Dictionary<Guid, Segment> ConfirmLocalConsensus(
        IReadOnlyList<QueuedEpisode> episodes,
        IReadOnlyDictionary<Guid, uint[]> fingerprints,
        IReadOnlyDictionary<Guid, Segment> candidates,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        _analysisMode = mode;
        if (candidates.Count == 0)
        {
            return [];
        }

        var orderedEpisodes = episodes
            .Where(episode =>
                fingerprints.TryGetValue(episode.EpisodeId, out var fingerprint) &&
                fingerprint.Length > 0)
            .OrderBy(episode => episode.EpisodeNumber)
            .ThenBy(episode => episode.EpisodeId)
            .ToList();
        var maximumGroupSize = Math.Min(_config.SettledSeasonChromaprintConsensusEpisodes, orderedEpisodes.Count);

        var localCandidates = new Dictionary<ConsensusRangeKey, Segment>();
        var matchingNeighbors = new Dictionary<ConsensusRangeKey, HashSet<ConsensusRangeKey>>();

        for (var leftIndex = 0; leftIndex < orderedEpisodes.Count; leftIndex++)
        {
            var leftEpisode = orderedEpisodes[leftIndex];
            var leftFingerprint = fingerprints[leftEpisode.EpisodeId];
            var lastNeighbor = Math.Min(orderedEpisodes.Count - 1, leftIndex + ConsensusNeighborRadius);
            for (var rightIndex = leftIndex + 1; rightIndex <= lastNeighbor; rightIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rightEpisode = orderedEpisodes[rightIndex];
                var rightFingerprint = fingerprints[rightEpisode.EpisodeId];

                var (leftCandidate, rightCandidate) = CompareEpisodes(
                    leftEpisode.EpisodeId,
                    leftFingerprint,
                    rightEpisode.EpisodeId,
                    rightFingerprint);

                var leftCandidateValid = leftCandidate.Valid &&
                    leftCandidate.Duration <= GetMaximumSegmentDuration(leftEpisode);
                var rightCandidateValid = rightCandidate.Valid &&
                    rightCandidate.Duration <= GetMaximumSegmentDuration(rightEpisode);

                if (mode == AnalysisMode.Credits)
                {
                    leftCandidate.Start += leftEpisode.CreditsFingerprintStart;
                    leftCandidate.End += leftEpisode.CreditsFingerprintStart;
                    rightCandidate.Start += rightEpisode.CreditsFingerprintStart;
                    rightCandidate.End += rightEpisode.CreditsFingerprintStart;
                }

                if (leftCandidateValid)
                {
                    KeepBetterCandidate(localCandidates, leftCandidate);
                }

                if (rightCandidateValid)
                {
                    KeepBetterCandidate(localCandidates, rightCandidate);
                }

                if (leftCandidateValid && rightCandidateValid)
                {
                    var leftRange = GetConsensusRangeKey(leftCandidate);
                    var rightRange = GetConsensusRangeKey(rightCandidate);
                    AddNeighbor(matchingNeighbors, leftRange, rightRange);
                    AddNeighbor(matchingNeighbors, rightRange, leftRange);
                }
            }
        }

        // Consensus confirms the season-selected ranges; it must not introduce a new endpoint
        // from a local pair that the season comparison itself did not select.
        foreach (var candidate in candidates.Values)
        {
            KeepBetterCandidate(localCandidates, candidate);
        }

        // Pairwise comparisons can find slightly different endpoints for the same opening in
        // different episode pairs. Join those compatible ranges within each episode so that
        // their neighboring pair matches contribute to one local group. Requiring the ranges
        // to overlap by most of the longer range keeps a longer cold open from borrowing the
        // consensus of a shorter intro that merely starts at the same time.
        foreach (var episodeRanges in localCandidates.Values.GroupBy(candidate => candidate.EpisodeId))
        {
            var ranges = episodeRanges.ToArray();
            for (var leftIndex = 0; leftIndex < ranges.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < ranges.Length; rightIndex++)
                {
                    if (!AreConsensusRangesCompatible(ranges[leftIndex], ranges[rightIndex]))
                    {
                        continue;
                    }

                    var leftRange = GetConsensusRangeKey(ranges[leftIndex]);
                    var rightRange = GetConsensusRangeKey(ranges[rightIndex]);
                    AddNeighbor(matchingNeighbors, leftRange, rightRange);
                    AddNeighbor(matchingNeighbors, rightRange, leftRange);
                }
            }
        }

        var groups = new List<List<ConsensusRangeKey>>();
        var visited = new HashSet<ConsensusRangeKey>();
        foreach (var rangeKey in matchingNeighbors.Keys)
        {
            if (!visited.Add(rangeKey))
            {
                continue;
            }

            var group = new List<ConsensusRangeKey>();
            var pending = new Stack<ConsensusRangeKey>();
            pending.Push(rangeKey);
            while (pending.TryPop(out var currentRangeKey))
            {
                group.Add(currentRangeKey);
                foreach (var neighborKey in matchingNeighbors[currentRangeKey])
                {
                    if (visited.Add(neighborKey))
                    {
                        pending.Push(neighborKey);
                    }
                }
            }

            groups.Add(group);
        }

        // A season may contain multiple themes. When there are multiple comparably supported
        // patterns, lower the configured target to the smallest such group so each pattern can
        // qualify. A small, isolated pattern cannot lower the target for a larger group.
        var groupEpisodeCounts = groups
            .Select(group => group.Select(key => key.EpisodeId).Distinct().Count())
            .Where(count => count > 0)
            .ToArray();
        var largestGroupSize = groupEpisodeCounts.DefaultIfEmpty(0).Max();
        var comparableGroupSizes = groupEpisodeCounts
            .Where(count => count * 2 >= largestGroupSize)
            .ToArray();
        var minimumGroupSize = comparableGroupSizes.Length > 1
            ? Math.Min(maximumGroupSize, comparableGroupSizes.Min())
            : maximumGroupSize;
        var supermajoritySize =
            ((orderedEpisodes.Count * ConsensusSupermajorityNumerator) + (ConsensusSupermajorityDenominator - 1)) /
            ConsensusSupermajorityDenominator;
        if (largestGroupSize >= supermajoritySize)
        {
            minimumGroupSize = Math.Min(minimumGroupSize, largestGroupSize);
        }

        minimumGroupSize = Math.Max(
            PluginConfiguration.MinimumSettledSeasonChromaprintConsensusEpisodes,
            minimumGroupSize);

        var supportedRanges = new HashSet<ConsensusRangeKey>();
        foreach (var group in groups)
        {
            if (group.Select(key => key.EpisodeId).Distinct().Count() < minimumGroupSize)
            {
                continue;
            }

            foreach (var memberRange in group)
            {
                supportedRanges.Add(memberRange);
            }
        }

        var confirmed = new Dictionary<Guid, Segment>();

        foreach (var candidate in candidates.Values)
        {
            if (supportedRanges.Contains(GetConsensusRangeKey(candidate)))
            {
                KeepBetterCandidate(confirmed, candidate);
            }
        }

        var removed = candidates.Keys.Count(id => !confirmed.ContainsKey(id));
        if (removed > 0)
        {
            LogCandidatesWithoutLocalConsensus(_logger, mode, minimumGroupSize, removed, confirmed.Count);
        }

        return confirmed;
    }

    private static ConsensusRangeKey GetConsensusRangeKey(Segment candidate)
        => new(
            candidate.EpisodeId,
            (long)Math.Round(candidate.Start, MidpointRounding.AwayFromZero),
            (long)Math.Round(candidate.End, MidpointRounding.AwayFromZero));

    private static bool AreConsensusRangesCompatible(Segment left, Segment right)
    {
        var overlap = Math.Max(0, Math.Min(left.End, right.End) - Math.Max(left.Start, right.Start));
        var longerDuration = Math.Max(left.Duration, right.Duration);
        return longerDuration > 0 && overlap / longerDuration >= ConsensusMinimumRangeOverlap;
    }

    private static void AddNeighbor(
        Dictionary<ConsensusRangeKey, HashSet<ConsensusRangeKey>> matchingNeighbors,
        ConsensusRangeKey rangeKey,
        ConsensusRangeKey neighborKey)
    {
        if (!matchingNeighbors.TryGetValue(rangeKey, out var neighbors))
        {
            neighbors = [];
            matchingNeighbors.Add(rangeKey, neighbors);
        }

        neighbors.Add(neighborKey);
    }

    private void KeepBetterCandidate(Dictionary<ConsensusRangeKey, Segment> candidates, Segment candidate)
    {
        var rangeKey = GetConsensusRangeKey(candidate);
        if (!candidates.TryGetValue(rangeKey, out var saved) ||
            IsBetterCandidate(candidate, saved, _analysisMode, _config.AnchorRecapToColdOpen, _config.EndSnapThreshold))
        {
            candidates[rangeKey] = candidate;
        }
    }

    private void KeepBetterCandidate(Dictionary<Guid, Segment> candidates, Segment candidate)
    {
        if (!candidates.TryGetValue(candidate.EpisodeId, out var saved) ||
            IsBetterCandidate(candidate, saved, _analysisMode, _config.AnchorRecapToColdOpen, _config.EndSnapThreshold))
        {
            candidates[candidate.EpisodeId] = candidate;
        }
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
    /// <param name="configuration">Plugin configuration containing the mode-specific minimum duration.</param>
    /// <returns>Minimum region duration in seconds.</returns>
    internal static double GetMinimumRegionDuration(AnalysisMode mode, PluginConfiguration configuration)
        => mode switch
        {
            AnalysisMode.Recap => RecapCardMinimumDuration,
            AnalysisMode.Credits => configuration.MinimumCreditsDuration,
            _ => configuration.MinimumIntroDuration,
        };

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
    /// for the same episode. Longer wins. The exception is an anchored recap: a start beyond the
    /// start snap threshold means a sting past the cold open was found, and it beats a candidate
    /// that snaps to 0:00 whichever is longer.
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

        if (bestStart < 0 || (bestEnd - bestStart) * ChromaprintConstants.SampleDuration < GetMinimumRegionDuration(_analysisMode, _config))
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
    private Dictionary<uint, int> CreateInvertedIndex(Guid id, uint[] fingerprint)
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Caught fingerprint error for \"{Path}\"")]
    private partial void LogCaughtFingerprintError(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromaprint fingerprint scan for {Episode} (\"{Path}\") timed out. " + FFmpegProcessRunner.ScanTimeoutHint)]
    private partial void LogFingerprintTimeout(Exception exception, string episode, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error adjusting the {Mode} boundaries for {Episode}")]
    private partial void LogErrorAdjustingIntro(Exception exception, AnalysisMode mode, string episode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Unable to find a shared introduction sequence between {LHS} and {RHS}")]
    private partial void LogSharedIntroNotFound(Guid lhs, Guid rhs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discarding the {Mode} region shared between {LHS} and {RHS}: {Duration:F2}s exceeds the maximum of {Maximum}s")]
    private partial void LogSharedRegionTooLong(Guid lhs, Guid rhs, AnalysisMode mode, double duration, int maximum);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Mode} Chromaprint consensus removed {RemovedCount} candidate(s) without range-specific support from a {MinimumGroupSize}-episode local group; {ConfirmedCount} candidate(s) remain")]
    private static partial void LogCandidatesWithoutLocalConsensus(ILogger logger, AnalysisMode mode, int minimumGroupSize, int removedCount, int confirmedCount);

    private readonly record struct ConsensusRangeKey(Guid EpisodeId, long StartSecond, long EndSecond);
}

/// <summary>
/// The outcome of a season-wide chromaprint comparison.
/// </summary>
/// <param name="Candidates">The raw candidate per episode id, in file seconds.</param>
/// <param name="FingerprintFailures">The episodes that still needed analysis and whose fingerprint failed.</param>
internal sealed record ChromaprintCandidates(Dictionary<Guid, Segment> Candidates, HashSet<Guid> FingerprintFailures);
