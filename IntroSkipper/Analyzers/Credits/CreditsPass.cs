// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Runs credits analysis for a season, trusting recognized credits chapters by default
/// and combining analyzer candidates for episodes without a chapter result.
/// </summary>
/// <remarks>
/// With chapter enhancement enabled, chapter matches, the black-frame scan and the
/// season-wide chromaprint comparison each contribute a candidate;
/// <see cref="CreditsCandidateCombiner"/> merges the ones that overlap or nearly touch and
/// keeps the rest apart. Otherwise chapter matches settle an episode first. Settled episodes
/// may still supply reference fingerprints for siblings without credits chapters, but their
/// results are not replaced. Times are adjusted once per stored segment and every episode
/// is written once.
/// </remarks>
/// <param name="loggerFactory">Logger factory for the analyzers.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="config">Plugin configuration.</param>
internal sealed partial class CreditsPass(
    ILoggerFactory loggerFactory,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    IIntroSkipperDatabase database,
    PluginConfiguration config)
{
    private const AnalysisMode Mode = AnalysisMode.Credits;

    private readonly ILogger<CreditsPass> _logger = loggerFactory.CreateLogger<CreditsPass>();
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly PluginConfiguration _config = config;

    /// <summary>
    /// Analyzes the season's credits.
    /// </summary>
    /// <remarks>
    /// Per-season BlackFrame and available Chromaprint actions bypass chapter matching.
    /// Chapter and unavailable Chromaprint actions follow the default chapter-first policy,
    /// including the enhancement option. An already-analyzed episode is reconsidered only
    /// when a new chromaprint candidate reaches outside its stored credits; authoritative
    /// chapters still prevent replacement.
    /// </remarks>
    /// <param name="items">The season's queued episodes, analyzed or not.</param>
    /// <param name="action">The season's analyzer action for credits.</param>
    /// <param name="ffmpegValid">Whether FFmpeg supports chromaprint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when every episode has been written or marked failed.</returns>
    public async Task RunAsync(IReadOnlyList<QueuedEpisode> items, AnalyzerAction action, bool ffmpegValid, CancellationToken cancellationToken)
    {
        var chromaprintAvailable = ffmpegValid && items.Count > 1;
        var restriction = action switch
        {
            AnalyzerAction.BlackFrame => action,
            AnalyzerAction.Chromaprint when chromaprintAvailable => action,
            _ => AnalyzerAction.Default,
        };
        var useChapter = restriction is AnalyzerAction.Default;
        var useBlackFrame = restriction is AnalyzerAction.Default or AnalyzerAction.BlackFrame;
        var useChromaprint = chromaprintAvailable && restriction is AnalyzerAction.Default or AnalyzerAction.Chromaprint;

        var chapter = useChapter ? new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>(), _ffmpegService, _database, _config) : null;
        var detectBlackFrameCredits = useBlackFrame ? CreateBlackFrameDetector() : null;
        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config, Mode, _ffmpegService);
        var pendingItems = items.Where(e => e.NeedsAnalysis(Mode)).ToList();
        HashSet<Guid> chapterHandled = [];
        HashSet<Guid> unavailableReferences = [];
        if (chapter is not null && !_config.EnhanceChapterCredits)
        {
            foreach (var episode in pendingItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var candidates = chapter.FindChapterCandidates(episode, Mode)
                        .Select(c => new AttributedSegment(c, SegmentSource.Chapter)).ToList();
                    if (candidates.Count == 0)
                    {
                        continue;
                    }

                    chapterHandled.Add(episode.EpisodeId);
                    var state = await StoreCandidatesAsync(episode, candidates, timeAdjustmentHelper, false, cancellationToken).ConfigureAwait(false);
                    episode.SetAnalyzed(Mode, state);
                    if (state != EpisodeState.Analyzed)
                    {
                        unavailableReferences.Add(episode.EpisodeId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    chapterHandled.Add(episode.EpisodeId);
                    unavailableReferences.Add(episode.EpisodeId);
                    episode.SetAnalyzed(Mode, EpisodeState.AnalysisFailed);
                    LogErrorAnalyzingCredits(ex, episode.Name);
                }
            }
        }

        Dictionary<Guid, Segment> chromaprintCandidates = [];
        HashSet<Guid> fingerprintFailures = [];
        if (useChromaprint && pendingItems.Any(e => !chapterHandled.Contains(e.EpisodeId)))
        {
            var comparisonItems = items.Where(e => !unavailableReferences.Contains(e.EpisodeId)).ToList();
            try
            {
                (chromaprintCandidates, fingerprintFailures) = await new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, _config)
                    .FindCandidatesAsync(comparisonItems, Mode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The season-wide comparison failed as a whole; the other analyzers still
                // write what they find below, and an episode they find nothing for stays
                // retriable.
                LogChromaprintComparisonFailed(ex);
                fingerprintFailures = [.. comparisonItems.Where(e => e.NeedsAnalysis(Mode)).Select(e => e.EpisodeId)];
            }
        }

        foreach (var episode in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The chromaprint comparison may pad with a user-provided neighbour; its
            // candidate is only there for the siblings.
            var hasChromaprintCandidate = chromaprintCandidates.TryGetValue(episode.EpisodeId, out var chromaprintCandidate);
            if (chapterHandled.Contains(episode.EpisodeId) || episode.GetAnalyzed(Mode) == EpisodeState.UserProvided || (!episode.NeedsAnalysis(Mode) && !hasChromaprintCandidate))
            {
                continue;
            }

            // An already-analyzed sibling re-derived from a changed season is re-scanned only
            // when its new shared-audio candidate reaches outside what is stored; otherwise
            // the combination could not change and the black-frame scan is skipped.
            if (!episode.NeedsAnalysis(Mode) && await IsCoveredByStoredCreditsAsync(episode, chromaprintCandidate!, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // A fingerprint failure costs the episode its shared-audio candidate. A result from
            // the other analyzers settles it like any other; with no result at all the episode
            // stays failed so the next scan retries instead of recording a false "no credits".
            var fingerprintFailed = fingerprintFailures.Contains(episode.EpisodeId);

            try
            {
                var candidates = new List<AttributedSegment>();
                if (chapter is not null && (_config.EnhanceChapterCredits || !episode.NeedsAnalysis(Mode)))
                {
                    candidates.AddRange(chapter.FindChapterCandidates(episode, Mode).Select(c => new AttributedSegment(c, SegmentSource.Chapter)));
                    if (!_config.EnhanceChapterCredits && candidates.Count > 0)
                    {
                        continue;
                    }
                }

                if (hasChromaprintCandidate)
                {
                    candidates.Add(new AttributedSegment(chromaprintCandidate!, SegmentSource.Chromaprint));
                }

                var (_, windowEnd) = episode.GetFingerprintRange(Mode);
                var minimumDuration = _config.MinimumCreditsDuration;

                // The whole credits window, every time: black credits can sit anywhere in it,
                // before, between or after the other candidates, and the keyframe scan is cached
                // so only the first analysis of an episode pays for it.
                if (detectBlackFrameCredits is not null)
                {
                    var candidate = await detectBlackFrameCredits(episode, cancellationToken).ConfigureAwait(false);
                    if (candidate is not null)
                    {
                        candidates.Add(new AttributedSegment(candidate, SegmentSource.BlackFrame));
                    }
                }

                var state = await StoreCandidatesAsync(
                    episode,
                    CreditsCandidateCombiner.Combine(candidates, windowEnd, minimumDuration),
                    timeAdjustmentHelper,
                    fingerprintFailed,
                    cancellationToken).ConfigureAwait(false);
                episode.SetAnalyzed(Mode, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                episode.SetAnalyzed(Mode, EpisodeState.AnalysisFailed);
                LogErrorAnalyzingCredits(ex, episode.Name);
            }
        }
    }

    private async Task<EpisodeState> StoreCandidatesAsync(
        QueuedEpisode episode,
        IReadOnlyList<AttributedSegment> candidates,
        TimeAdjustmentHelper timeAdjustmentHelper,
        bool fingerprintFailed,
        CancellationToken cancellationToken)
    {
        var adjusted = new List<AttributedSegment>();
        foreach (var (segment, source) in candidates)
        {
            // A chapter-only range already sits on authored boundaries; do not snap it
            // to another chapter, but retain the configured playback adjustments.
            var adjustedSegment = await timeAdjustmentHelper
                .AdjustIntroTimesAsync(episode, segment, source == SegmentSource.Chapter ? false : null, cancellationToken)
                .ConfigureAwait(false);
            if (adjustedSegment.Valid)
            {
                adjusted.Add(new AttributedSegment(adjustedSegment, source));
            }
        }

        if (adjusted.Count == 0)
        {
            if (fingerprintFailed)
            {
                return EpisodeState.AnalysisFailed;
            }

            LogNoCreditsFound(episode.Name);
            await _database.ReplaceAutoSegmentsAsync(episode.EpisodeId, Mode, [], episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
            return EpisodeState.NoSegments;
        }

        foreach (var (segment, source) in adjusted)
        {
            LogFoundCredits(episode.Name, segment.Start, segment.End, source);
        }

        await _database.ReplaceAutoSegmentsAsync(episode.EpisodeId, Mode, adjusted, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
        return EpisodeState.Analyzed;
    }

    /// <summary>
    /// The black-frame candidate producer: the legacy analyzer under its toggle, otherwise the
    /// current one. One instance per season, since the legacy analyzer carries search state
    /// between episodes.
    /// </summary>
    private Func<QueuedEpisode, CancellationToken, Task<Segment?>> CreateBlackFrameDetector()
    {
        if (_config.UseLegacyBlackFrameAnalyzer)
        {
            var legacy = new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>(), _ffmpegService, _config);
            return legacy.DetectCreditsAsync;
        }

        var current = new CreditsBlackFrameAnalyzer(_loggerFactory.CreateLogger<CreditsBlackFrameAnalyzer>(), _ffmpegService, _config);
        return current.DetectCreditsAsync;
    }

    /// <summary>
    /// Whether a stored active credits row already contains the raw candidate, allowing for
    /// the boundary movement time adjustment applies before storing.
    /// </summary>
    private async Task<bool> IsCoveredByStoredCreditsAsync(QueuedEpisode episode, Segment candidate, CancellationToken cancellationToken)
    {
        var tolerance = Math.Max(_config.AdjustWindowInward, _config.AdjustWindowOutward);
        var rows = await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return rows.Any(row =>
            row.Type == Mode &&
            row.State == SegmentState.Active &&
            TickConversions.ToSeconds(row.StartTicks) <= candidate.Start + tolerance &&
            TickConversions.ToSeconds(row.EndTicks) >= candidate.End - tolerance);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No credits found for {Episode}")]
    private partial void LogNoCreditsFound(string episode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found credits for {Episode} at {Start:F2}s to {End:F2}s ({Source})")]
    private partial void LogFoundCredits(string episode, double start, double end, SegmentSource source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error analyzing {Episode} for credits")]
    private partial void LogErrorAnalyzingCredits(Exception ex, string episode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromaprint credits comparison failed; continuing with the other analyzers")]
    private partial void LogChromaprintComparisonFailed(Exception ex);
}
