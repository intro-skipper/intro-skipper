// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Runs credits analysis for a season, trusting recognized credits chapters that no story
/// follows by default and combining analyzer candidates for the other episodes.
/// </summary>
/// <remarks>
/// With chapter enhancement enabled, or when story follows the credits chapter, chapter matches,
/// the keyframe analyzer (black rolls from black-frame evidence and card credits from keyframe
/// visuals, over one keyframe scan) and the season-wide chromaprint comparison each contribute
/// candidates; <see cref="CreditsCandidateCombiner"/> merges the ones that overlap or nearly
/// touch and keeps the rest apart. Otherwise chapter matches settle an episode first. Episodes the
/// chapter pre-pass decided, whatever the outcome, still take part in the comparison as
/// references for siblings without an authoritative chapter, but their results are not replaced.
/// Times are adjusted once per stored segment and every episode is written once.
/// </remarks>
/// <param name="loggerFactory">Logger factory for the analyzers.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="config">Plugin configuration.</param>
/// <param name="useLocalConsensus">Whether this run is a settled-season re-analysis.</param>
internal sealed partial class CreditsPass(
    ILoggerFactory loggerFactory,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    IntroSkipperDatabase database,
    PluginConfiguration config,
    bool useLocalConsensus = false)
{
    private const AnalysisMode Mode = AnalysisMode.Credits;

    // How far before a Chromaprint start the black it opens on may begin, and how far from the
    // start that black may end (see StartOnBlackBeforeAsync).
    private const double BlackBeforeLookBackSeconds = KeyframeScan.MaximumSceneMergeGapSeconds;
    private const double BlackBeforeToleranceSeconds = 1;

    private readonly ILogger<CreditsPass> _logger = loggerFactory.CreateLogger<CreditsPass>();
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly IntroSkipperDatabase _database = database;
    private readonly PluginConfiguration _config = config;
    private readonly bool _useLocalConsensus = useLocalConsensus;

    /// <summary>
    /// Analyzes the season's credits.
    /// </summary>
    /// <remarks>
    /// Per-season BlackFrame and available Chromaprint actions bypass chapter matching; a
    /// BlackFrame action restricts the pass to the keyframe analyzer, which keeps all of its candidates.
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
        var detectKeyframeCredits = useBlackFrame ? CreateKeyframeDetector() : null;
        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config, Mode, _ffmpegService);
        var pendingItems = items.Where(e => e.NeedsAnalysis(Mode)).ToList();
        Dictionary<Guid, ChapterCredits> chapters = chapter is null ? [] : items.ToDictionary(episode => episode.EpisodeId, episode => FindChapterCredits(chapter, episode));

        // A chapter match owns the episode even if admission rejects its write: falling through
        // to an empty combined result could otherwise clear standing Credits rows. Offset-
        // consumed, rejected, and failed episodes remain unsettled according to their state.
        HashSet<Guid> chapterHandled = [];
        if (chapter is not null)
        {
            foreach (var episode in pendingItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var credits = chapters[episode.EpisodeId];
                    if (!credits.Settles)
                    {
                        continue;
                    }

                    var chapterState = await chapter.StoreMatchesAsync(episode, Mode, credits.Matches, timeAdjustmentHelper, SegmentSource.Chapter, cancellationToken).ConfigureAwait(false);
                    episode.SetAnalyzed(Mode, chapterState);
                    chapterHandled.Add(episode.EpisodeId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    chapterHandled.Add(episode.EpisodeId);
                    episode.SetAnalyzed(Mode, EpisodeState.AnalysisFailed);
                    if (_logger.IsEnabled(LogLevel.Error))
                    {
                        var hint = TimeoutHint(ex);
                        LogErrorAnalyzingCredits(ex, episode.Name, hint);
                    }
                }
            }
        }

        // The whole season takes part in the comparison. A chapter-handled episode's audio is
        // a valid reference whatever its outcome, and dropping one can leave a two-episode
        // season with nothing to compare against.
        Dictionary<Guid, Segment> chromaprintCandidates = [];
        HashSet<Guid> fingerprintFailures = [];
        if (useChromaprint && pendingItems.Any(e => !chapterHandled.Contains(e.EpisodeId)))
        {
            try
            {
                // A Chromaprint region that joins one before a chapter's end would be capped there
                // and lose what lies after it, so the comparison gets each episode's hard boundaries,
                // relative to its credits window, as the combiner will draw them.
                var hardBoundaries = chapter is null
                    ? null
                    : items.ToDictionary(
                        episode => episode.EpisodeId,
                        episode => (IReadOnlyList<double>)[.. CreditsCandidateCombiner.HardBoundaries(chapters[episode.EpisodeId].Candidates, episode.GetFingerprintRange(Mode).End).Select(boundary => boundary - episode.CreditsFingerprintStart)]);
                (chromaprintCandidates, fingerprintFailures) = await new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, _config, _useLocalConsensus, hardBoundaries)
                    .FindCandidatesAsync(items, Mode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The season-wide comparison failed as a whole; the other analyzers still
                // write what they find below, and an episode they find nothing for stays
                // retriable.
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    var hint = TimeoutHint(ex);
                    LogChromaprintComparisonFailed(ex, hint);
                }

                fingerprintFailures = [.. items.Where(e => e.NeedsAnalysis(Mode)).Select(e => e.EpisodeId)];
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
            if (!episode.NeedsAnalysis(Mode) && IsCoveredByStoredCredits(await StandingCreditsAsync(episode, cancellationToken).ConfigureAwait(false), chromaprintCandidate!))
            {
                continue;
            }

            // A failed analyzer costs the episode its candidate, not the pass. A result from the
            // other analyzers settles the episode like any other; with no result at all it is
            // marked failed so the next pass retries instead of recording a false "no credits".
            var analyzerFailed = fingerprintFailures.Contains(episode.EpisodeId);

            try
            {
                var candidates = new List<AttributedSegment>();
                if (chapters.TryGetValue(episode.EpisodeId, out var credits))
                {
                    if (!episode.NeedsAnalysis(Mode) && credits.Settles)
                    {
                        // A settled sibling's authoritative chapter stays over the re-derived
                        // candidate. Pending episodes reached here only because the pre-pass found
                        // no authoritative chapter for them.
                        continue;
                    }

                    candidates.AddRange(credits.Candidates);
                }

                var (_, windowEnd) = episode.GetFingerprintRange(Mode);
                var minimumDuration = _config.MinimumCreditsDuration;

                // The whole credits window, every time: black credits and card credits can sit anywhere in
                // it, before, between or after the other candidates, and the keyframe scan is cached so only
                // the first analysis of an episode pays for it.
                if (detectKeyframeCredits is not null)
                {
                    try
                    {
                        candidates.AddRange(await detectKeyframeCredits(episode, cancellationToken).ConfigureAwait(false));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                        {
                            var hint = TimeoutHint(ex);
                            LogKeyframeAnalysisFailed(ex, episode.Name, hint);
                        }

                        // Without the keyframe result, the other candidates alone could lose the
                        // roll the standing credits hold, so a failed keyframe scan never replaces
                        // them. A settled sibling stays settled; a pending episode is marked failed
                        // and the next pass retries it.
                        if ((await StandingCreditsAsync(episode, cancellationToken).ConfigureAwait(false)).Count > 0)
                        {
                            if (episode.NeedsAnalysis(Mode))
                            {
                                episode.SetAnalyzed(Mode, EpisodeState.AnalysisFailed);
                            }

                            continue;
                        }

                        analyzerFailed = true;
                    }
                }

                // After the keyframe scan, so a scan that keeps the standing credits costs no probe.
                if (hasChromaprintCandidate)
                {
                    var moved = await StartOnBlackBeforeAsync(episode, chromaprintCandidate!, cancellationToken).ConfigureAwait(false);

                    // A start moved across a chapter's end would be capped there and could drop the
                    // credits after it, so it stays on its side of every boundary.
                    candidates.Add(new AttributedSegment(
                        CreditsCandidateCombiner.HasBoundaryBetween(CreditsCandidateCombiner.HardBoundaries(candidates, windowEnd), moved.Start, chromaprintCandidate!.Start) ? chromaprintCandidate : moved,
                        SegmentSource.Chromaprint));
                }

                var state = await StoreCandidatesAsync(
                    episode,
                    CreditsCandidateCombiner.Combine(candidates, windowEnd, minimumDuration),
                    timeAdjustmentHelper,
                    analyzerFailed,
                    cancellationToken).ConfigureAwait(false);
                episode.SetAnalyzed(Mode, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                episode.SetAnalyzed(Mode, EpisodeState.AnalysisFailed);
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    var hint = TimeoutHint(ex);
                    LogErrorAnalyzingCredits(ex, episode.Name, hint);
                }
            }
        }
    }

    private async Task<EpisodeState> StoreCandidatesAsync(
        QueuedEpisode episode,
        IReadOnlyList<AttributedSegment> candidates,
        TimeAdjustmentHelper timeAdjustmentHelper,
        bool analyzerFailed,
        CancellationToken cancellationToken)
    {
        var adjusted = new List<AttributedSegment>();
        foreach (var (segment, source) in candidates)
        {
            // A chapter range sits on authored marks: it is not snapped to another chapter,
            // silence or keyframe, but the configured offsets apply.
            var adjustedSegment = await timeAdjustmentHelper
                .AdjustIntroTimesAsync(episode, segment, source == SegmentSource.Chapter, cancellationToken)
                .ConfigureAwait(false);
            if (adjustedSegment.Valid)
            {
                adjusted.Add(new AttributedSegment(adjustedSegment, source));
            }
        }

        if (adjusted.Count == 0)
        {
            if (analyzerFailed)
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

        // A result settles the episode even when admission refuses all of it. The refusal comes
        // from a tombstone, a user row or the intro overlap rule, and every retry would meet it again.
        await _database.ReplaceAutoSegmentsAsync(episode.EpisodeId, Mode, adjusted, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
        return EpisodeState.Analyzed;
    }

    /// <summary>
    /// Finds the episode's credits chapter matches and whether they settle it without combined
    /// detection: enhancement is off, a chapter matched and no story follows it.
    /// </summary>
    /// <remarks>
    /// Story follows when at least the minimum credits duration runs from the end of the matches
    /// to the first preview chapter after them or the credits window end, whichever comes first.
    /// That room can hold closing cards after an epilogue, which a mid-episode ending chapter
    /// leaves out. The window ends before the file only under ProbeAudioDuration, which is for
    /// runtimes padded past the audio, so room past it is not counted as story, though the
    /// keyframe scan would still read black frames there.
    /// </remarks>
    private ChapterCredits FindChapterCredits(ChapterAnalyzer chapter, QueuedEpisode episode)
    {
        var matches = chapter.FindChapterCandidates(episode, Mode);
        if (_config.EnhanceChapterCredits || matches.Count == 0)
        {
            return new ChapterCredits(matches, Settles: false);
        }

        var end = matches.Max(m => m.End);
        var windowEnd = episode.GetFingerprintRange(Mode).End;
        var storyEnd = Math.Min(chapter.FindFirstMatchStart(episode, AnalysisMode.Preview, end) ?? windowEnd, windowEnd);
        return new ChapterCredits(matches, Settles: storyEnd - end < _config.MinimumCreditsDuration);
    }

    /// <summary>
    /// The keyframe detector: the legacy black-frame analyzer under its toggle, otherwise the
    /// keyframe analyzer, which also finds the card candidate. One instance per season, since the
    /// legacy analyzer carries search state between episodes.
    /// </summary>
    private Func<QueuedEpisode, CancellationToken, Task<IReadOnlyList<AttributedSegment>>> CreateKeyframeDetector()
    {
        if (_config.UseLegacyBlackFrameAnalyzer)
        {
            var legacy = new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>(), _ffmpegService, _config);
            return async (episode, cancellationToken) => await legacy.DetectCreditsAsync(episode, cancellationToken).ConfigureAwait(false) is { } credits
                ? [new AttributedSegment(credits, SegmentSource.BlackFrame)]
                : [];
        }

        return new KeyframeAnalyzer(_loggerFactory.CreateLogger<KeyframeAnalyzer>(), _ffmpegService, _cacheService, _config).DetectCreditsAsync;
    }

    /// <summary>
    /// The episode's active credits rows, whoever wrote them.
    /// </summary>
    private async Task<IReadOnlyList<DbSegment>> StandingCreditsAsync(QueuedEpisode episode, CancellationToken cancellationToken)
    {
        var rows = await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(row => row.Type == Mode && row.State == SegmentState.Active)];
    }

    /// <summary>
    /// Whether one of the standing credits rows already contains the raw candidate, allowing for
    /// the boundary movement time adjustment applies before storing.
    /// </summary>
    private bool IsCoveredByStoredCredits(IReadOnlyList<DbSegment> standing, Segment candidate)
    {
        var tolerance = Math.Max(_config.AdjustWindowInward, _config.AdjustWindowOutward);
        return standing.Any(row =>
            TickConversions.ToSeconds(row.StartTicks) <= candidate.Start + tolerance &&
            TickConversions.ToSeconds(row.EndTicks) >= candidate.End - tolerance);
    }

    /// <summary>
    /// The sentence a failure log appends when an ffmpeg scan timed out, naming the setting that
    /// raises the timeout. The season-level timeout warning shares it. Empty for any other failure.
    /// </summary>
    private static string TimeoutHint(Exception ex) =>
        ex is TimeoutException or FingerprintException { InnerException: TimeoutException }
            ? ". " + FFmpegProcessRunner.ScanTimeoutHint
            : string.Empty;

    /// <summary>
    /// Moves a Chromaprint credits candidate's start back to the black it opens on. Shared end
    /// titles often follow a cut to black that still carries each episode's own story sound, so the
    /// shared audio, and the match, starts only as the black ends. One blackdetect probe covers
    /// <see cref="BlackBeforeLookBackSeconds"/> before the start; the start moves to the beginning of
    /// the black that ends within <see cref="BlackBeforeToleranceSeconds"/> of it, counting intervals
    /// within that tolerance of each other as one black. A black that begins within that tolerance of
    /// where the probe begins may have begun earlier, so it moves nothing, and neither does a failed
    /// probe. The probe uses the configured black threshold and minimum.
    /// </summary>
    private async Task<Segment> StartOnBlackBeforeAsync(QueuedEpisode episode, Segment candidate, CancellationToken cancellationToken)
    {
        var (windowStart, _) = episode.GetFingerprintRange(Mode);
        var probe = new TimeRange(Math.Max(windowStart, candidate.Start - BlackBeforeLookBackSeconds), candidate.Start + BlackBeforeToleranceSeconds);
        BlackInterval[] intervals;
        try
        {
            intervals = await _ffmpegService.DetectBlackIntervalsAsync(episode, probe, _config.BlackFrameThreshold, _config.BlackFrameMinimumPercentage, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                var hint = TimeoutHint(ex);
                LogBlackBeforeProbeFailed(ex, episode.Name, hint);
            }

            return candidate;
        }

        // The intervals are relative to the credits fingerprint start. A title fading in can split
        // one black into pieces a few frames apart, so pieces within the tolerance of each other are
        // walked back as one black.
        var offset = episode.CreditsFingerprintStart;
        var reach = candidate.Start - BlackBeforeToleranceSeconds;
        double? start = null;
        foreach (var interval in intervals.Where(interval => interval.Start + offset < candidate.Start).OrderByDescending(interval => interval.Start))
        {
            if (interval.End + offset < reach)
            {
                break;
            }

            start = interval.Start + offset;
            reach = start.Value - BlackBeforeToleranceSeconds;
        }

        // A seek lands blackdetect's first frame up to a frame or more after the probe start, so a
        // black reported that close to it was already running when the probe began.
        return start is not { } blackStart || blackStart < probe.Start + BlackBeforeToleranceSeconds
            ? candidate
            : new Segment(candidate.EpisodeId) { Start = blackStart, End = candidate.End };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Black probe before the Chromaprint credits of {Episode} failed; keeping its start{Hint}")]
    private partial void LogBlackBeforeProbeFailed(Exception ex, string episode, string hint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No credits found for {Episode}")]
    private partial void LogNoCreditsFound(string episode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Found credits for {Episode} at {Start:F2}s to {End:F2}s ({Source})")]
    private partial void LogFoundCredits(string episode, double start, double end, SegmentSource source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error analyzing {Episode} for credits{Hint}")]
    private partial void LogErrorAnalyzingCredits(Exception ex, string episode, string hint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keyframe credits analysis failed for {Episode}{Hint}")]
    private partial void LogKeyframeAnalysisFailed(Exception ex, string episode, string hint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromaprint credits comparison failed; continuing with the other analyzers{Hint}")]
    private partial void LogChromaprintComparisonFailed(Exception ex, string hint);

    private readonly record struct ChapterCredits(IReadOnlyList<Segment> Matches, bool Settles)
    {
        public List<AttributedSegment> Candidates => [.. Matches.Select(match => new AttributedSegment(match, SegmentSource.Chapter))];
    }
}
