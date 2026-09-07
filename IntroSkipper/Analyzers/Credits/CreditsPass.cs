// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// Runs credits analysis for a season by collecting a candidate from every applicable
/// analyzer per episode and storing their combination.
/// </summary>
/// <remarks>
/// Unlike the first-wins analyzer chain the other modes use, no analyzer settles an episode
/// here. Chapter matches, the black-frame scan and the season-wide chromaprint comparison
/// each contribute a candidate; <see cref="CreditsCandidateCombiner"/> merges the ones that
/// overlap or nearly touch and keeps the rest apart. Times are adjusted once per stored
/// segment and every episode is written once.
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

    // The black-frame probe starts at the anchor rounded down to this grid, so a shared-audio
    // match that grows by a fingerprint step when a sibling arrives keeps its cache key.
    private const double ProbeAnchorGridSeconds = 10;

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
    /// A per-season BlackFrame or Chromaprint action restricts the pass to that analyzer's
    /// candidate; Chromaprint when it is unavailable, and Chapter, are treated as the
    /// default, since chapters always take part. Episodes are written when they need
    /// analysis or when the chromaprint comparison re-derived a candidate for them from a
    /// changed season.
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
        Dictionary<Guid, Segment> chromaprintCandidates = [];
        if (useChromaprint)
        {
            try
            {
                chromaprintCandidates = await new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, _config)
                    .FindCandidatesAsync(items, Mode, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The season-wide comparison failed as a whole; the other analyzers still
                // write what they find below, and an episode they find nothing for stays
                // retriable.
                LogChromaprintComparisonFailed(ex);
                foreach (var episode in items.Where(e => e.NeedsAnalysis(Mode)))
                {
                    episode.SetAnalyzed(Mode, EpisodeState.AnalysisFailed);
                }
            }
        }

        var timeAdjustmentHelper = new TimeAdjustmentHelper(_logger, _config, Mode, _ffmpegService);

        foreach (var episode in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The chromaprint comparison may pad with a user-provided neighbour; its
            // candidate is only there for the siblings.
            var hasChromaprintCandidate = chromaprintCandidates.TryGetValue(episode.EpisodeId, out var chromaprintCandidate);
            if (episode.GetAnalyzed(Mode) == EpisodeState.UserProvided || (!episode.NeedsAnalysis(Mode) && !hasChromaprintCandidate))
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
            var fingerprintFailed = episode.GetAnalyzed(Mode) == EpisodeState.AnalysisFailed;

            try
            {
                var candidates = new List<AttributedSegment>();
                if (chapter is not null)
                {
                    candidates.AddRange(chapter.FindChapterCandidates(episode, Mode).Select(c => new AttributedSegment(c, SegmentSource.Chapter)));
                }

                if (hasChromaprintCandidate)
                {
                    candidates.Add(new AttributedSegment(chromaprintCandidate!, SegmentSource.Chromaprint));
                }

                var (windowStart, windowEnd) = episode.GetFingerprintRange(Mode);
                var minimumDuration = _config.MinimumCreditsDuration;

                if (detectBlackFrameCredits is not null)
                {
                    var probe = await SelectBlackFrameProbeAsync(episode, candidates, windowStart, windowEnd, minimumDuration, cancellationToken).ConfigureAwait(false);
                    if (probe is not null)
                    {
                        var candidate = await detectBlackFrameCredits(probe, cancellationToken).ConfigureAwait(false);
                        if (candidate is not null)
                        {
                            candidates.Add(new AttributedSegment(candidate, SegmentSource.BlackFrame));
                        }
                    }
                }

                List<double> chapterStarts = [.. (Plugin.Instance?.GetChapters(episode.EpisodeId) ?? []).Select(c => TimeSpan.FromTicks(c.StartPositionTicks).TotalSeconds)];

                var adjusted = new List<AttributedSegment>();
                foreach (var (segment, source) in CreditsCandidateCombiner.Combine(candidates, windowEnd, minimumDuration, chapterStarts))
                {
                    // A chapter-only range already sits on chapter boundaries; the chapter
                    // analyzer skips chapter snapping for its own matches too.
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
                        continue;
                    }

                    LogNoCreditsFound(episode.Name);
                    await _database.ReplaceAutoSegmentsAsync(episode.EpisodeId, Mode, [], episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
                    episode.SetAnalyzed(Mode, EpisodeState.NoSegments);
                    continue;
                }

                foreach (var (segment, source) in adjusted)
                {
                    LogFoundCredits(episode.Name, segment.Start, segment.End, source);
                }

                await _database.ReplaceAutoSegmentsAsync(episode.EpisodeId, Mode, adjusted, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
                episode.SetAnalyzed(Mode, EpisodeState.Analyzed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                episode.SetAnalyzed(Mode, EpisodeState.AnalysisFailed);
                LogErrorAnalyzingCredits(ex, episode.Name);
            }
        }
    }

    /// <summary>
    /// Chooses the window the black-frame analyzer scans: the whole credits window when
    /// nothing else was found or when black frames precede the earliest other candidate, the
    /// tail after the latest candidate otherwise, and nothing when too little of the window
    /// remains for a credits scene (the combiner extends the candidate to the end instead).
    /// </summary>
    /// <remarks>
    /// A roll or a dubbing card usually follows the chapter or shared-audio credits, so the
    /// tail is where black frames are expected. A roll that starts before the credits music
    /// would be missed by a tail probe, so the seconds before the earliest candidate are
    /// checked first with a short bounded scan; black there sends the analyzer over the full
    /// window. The tail anchor is rounded down to a grid so a shared-audio match that grows
    /// by a fingerprint step when a sibling arrives keeps its cache key. The legacy analyzer
    /// always gets the full window: its binary search cannot resolve a tail under 20 s.
    /// </remarks>
    /// <returns>The episode, or a copy with the window moved to the tail, or <see langword="null"/> to skip the scan.</returns>
    private async Task<QueuedEpisode?> SelectBlackFrameProbeAsync(
        QueuedEpisode episode,
        IReadOnlyList<AttributedSegment> candidates,
        double windowStart,
        double windowEnd,
        int minimumDuration,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || _config.UseLegacyBlackFrameAnalyzer)
        {
            return episode;
        }

        var earliestStart = candidates.Min(c => c.Segment.Start);
        var lead = new TimeRange(Math.Max(windowStart, earliestStart - CreditDetectionPolicy.MaximumSceneMergeGapSeconds), earliestStart);
        if (lead.Duration > 0)
        {
            var leadFrames = await _ffmpegService
                .DetectBlackFramesAsync(episode, lead, _config.BlackFrameMinimumPercentage, _config.BlackFrameThreshold, Mode, cancellationToken)
                .ConfigureAwait(false);
            if (leadFrames.Length > 0)
            {
                return episode;
            }
        }

        var latestEnd = candidates.Max(c => c.Segment.End);
        if (CreditsCandidateCombiner.ReachesWindowEnd(latestEnd, windowEnd, minimumDuration))
        {
            return null;
        }

        var anchor = Math.Floor(latestEnd / ProbeAnchorGridSeconds) * ProbeAnchorGridSeconds;
        return episode.WithCreditsFingerprintStart(Math.Max(windowStart, anchor));
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
