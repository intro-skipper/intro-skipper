// SPDX-FileCopyrightText: 2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Common code shared by all media item analyzer tasks.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BaseItemAnalyzerTask"/> class.
/// </remarks>
/// <param name="logger">Task logger.</param>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="analyzerFactory">Factory used to create fresh queue managers per run.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="database">Segment database facade.</param>
public partial class BaseItemAnalyzerTask(
    ILogger logger,
    ILoggerFactory loggerFactory,
    AnalyzerTaskFactory analyzerFactory,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    IIntroSkipperDatabase database)
{
    /// <summary>
    /// Tolerance (seconds) when comparing an existing anime Preview's start to a newly-computed
    /// credits.End. Chromaprint timestamps are quantised to ~0.124 s and a sub-second delta has
    /// no user-visible effect, so treat "close enough" as equal for idempotency.
    /// </summary>
    private const double AnimePreviewStartTolerance = 0.5;

    private readonly ILogger _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly AnalyzerTaskFactory _analyzerFactory = analyzerFactory;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly IIntroSkipperDatabase _database = database;

    /// <summary>
    /// Gets the live plugin configuration. Jellyfin replaces the configuration object on save, so
    /// retaining a constructor-time snapshot can stamp analysis with a hash that no longer matches
    /// the analyzers or queue verifier.
    /// </summary>
    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Analyze all media items on the server.
    /// </summary>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="seasonsToAnalyze">Season IDs to analyze.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AnalyzeItemsAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? seasonsToAnalyze = null)
    {
        List<AnalysisMode> modes = [
            .. Config.ScanIntroduction ? [AnalysisMode.Introduction] : Array.Empty<AnalysisMode>(),
            .. Config.ScanCredits ? [AnalysisMode.Credits] : Array.Empty<AnalysisMode>(),
            .. Config.ScanRecap ? [AnalysisMode.Recap] : Array.Empty<AnalysisMode>(),
            .. Config.ScanPreview ? [AnalysisMode.Preview] : Array.Empty<AnalysisMode>(),
            .. Config.ScanCommercial ? [AnalysisMode.Commercial] : Array.Empty<AnalysisMode>()
        ];

        if (seasonsToAnalyze?.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var seasonFilter = seasonsToAnalyze?.ToHashSet();

        var queueManager = _analyzerFactory.CreateQueueManager();

        _ = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");
        var ffmpegValid = await queueManager.GetFfmpegValidAsync(cancellationToken).ConfigureAwait(false);

        var queue = (await queueManager.GetMediaInventoryAsync(seasonIds: seasonFilter, cancellationToken: cancellationToken).ConfigureAwait(false)).Items;

        if (seasonFilter is not null)
        {
            queue = queue.Where(kvp => seasonFilter.Contains(kvp.Key))
                         .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        int totalQueued = queue.Sum(kvp => kvp.Value.Count) * modes.Count;
        if (totalQueued == 0)
        {
            LogNoLibrariesSelected(_logger);
            return;
        }

        if (!ffmpegValid)
        {
            LogSkippingChromaprint(_logger);
        }

        int totalProcessed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Config.MaxParallelism),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(queue, options, async (season, ct) =>
        {
            IReadOnlyList<AnalysisMode> settledResetModes = [];

            var episodes = await queueManager.VerifyQueueAsync(season.Value, modes, ct).ConfigureAwait(false);
            if (episodes.Count == 0)
            {
                return;
            }

            var first = episodes[0];

            // Run settled-season reanalysis from scratch after no new episodes have been added
            // for the configured delay so segments first derived from a partial season are
            // recomputed against the full season.
            // Reuses the cached fingerprints, so this only re-runs the comparison, not the decode.
            var utcNow = DateTime.UtcNow;
            var episodeIds = episodes.Select(e => e.EpisodeId).ToArray();

            // One season-state read serves both the settle decision and every mode's
            // analyzer action below.
            var seasonStates = await _database.GetSettleReanalysisStatesAsync(first.SeasonId, ct).ConfigureAwait(false);
            if (SeasonReanalysisPlanner.IsSettledForReanalysis(episodes, Config, utcNow))
            {
                settledResetModes = GetSettleReanalysisModes(seasonStates, episodeIds, modes, ffmpegValid);
                if (settledResetModes.Count > 0)
                {
                    var resetModes = ExpandSettledResetModesForDerivedSegments(settledResetModes, Config.AnimePreviewFromCreditsEnd);
                    LogReanalyzingSettledSeason(_logger, first.SeasonNumber, first.SeriesName, episodes.Count);

                    // The reset journals its deletions' projections, so they propagate
                    // to Jellyfin even if the recompute finds nothing.
                    await _database.ResetItemsForReanalysisAsync(episodeIds, resetModes, ct).ConfigureAwait(false);
                    foreach (var episode in episodes)
                    {
                        foreach (var resetMode in resetModes)
                        {
                            if (episode.GetAnalyzed(resetMode) != EpisodeState.UserProvided)
                            {
                                episode.SetAnalyzed(resetMode, EpisodeState.NotAnalyzed);
                            }
                        }
                    }
                }
            }

            var completedSettledModes = new List<AnalysisMode>(settledResetModes.Count);

            try
            {
                foreach (var mode in modes)
                {
                    ct.ThrowIfCancellationRequested();
                    await AnalyzeItemsAsync(
                        episodes,
                        mode,
                        seasonStates.TryGetValue(mode, out var seasonState) ? seasonState.Action : AnalyzerAction.Default,
                        ffmpegValid,
                        ct).ConfigureAwait(false);
                    Interlocked.Add(ref totalProcessed, episodes.Count);

                    // Record only the modes we independently selected for reanalysis. A derived mode added
                    // by ExpandSettledResetModesForDerivedSegments (Preview from Credits) is reset and then
                    // regenerated as a side effect of its source mode's analysis, so its completion rides on
                    // the source mode's record and is intentionally not tracked separately here.
                    if (settledResetModes.Contains(mode))
                    {
                        completedSettledModes.Add(mode);
                    }

                    progress.Report((double)totalProcessed / totalQueued * 100);
                }
            }
            catch (OperationCanceledException)
            {
                LogAnalysisCanceled(_logger);
                throw;
            }
            catch (FingerprintException ex)
            {
                LogFingerprintExceptionDuringAnalysis(_logger, ex);
            }
            catch (TimeoutException ex)
            {
                LogFfmpegTimeoutDuringAnalysis(_logger, ex);
            }
            catch (Exception ex)
            {
                LogUnexpectedAnalysisError(_logger, ex);
                throw;
            }

            // No mirror push here: every write above journaled its item's projection
            // with the change, and the projection worker converges Jellyfin durably.
            if (completedSettledModes.Count > 0)
            {
                await _database.RecordSettleReanalysisAsync(first.SeasonId, completedSettledModes, episodeIds, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static List<AnalysisMode> GetSettleReanalysisModes(
        IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)> settleReanalysisStates,
        IReadOnlyCollection<Guid> episodeIds,
        IReadOnlyCollection<AnalysisMode> modes,
        bool ffmpegValid)
    {
        var resetModes = new List<AnalysisMode>(modes.Count);
        foreach (var mode in modes)
        {
            var stateExists = settleReanalysisStates.TryGetValue(mode, out var state);
            var action = stateExists ? state.Action : AnalyzerAction.Default;
            if (action != AnalyzerAction.None &&
                CanSettleReanalysisRun(mode, action, ffmpegValid) &&
                (!stateExists || AnalysisHelpers.ShouldSettleReanalyze(state.SettledReanalysisEpisodeIds, episodeIds)))
            {
                resetModes.Add(mode);
            }
        }

        return resetModes;
    }

    internal static IReadOnlyCollection<AnalysisMode> ExpandSettledResetModesForDerivedSegments(
        IReadOnlyList<AnalysisMode> modes,
        bool animePreviewFromCreditsEnd)
    {
        if (!animePreviewFromCreditsEnd ||
            !modes.Contains(AnalysisMode.Credits) ||
            modes.Contains(AnalysisMode.Preview))
        {
            return modes;
        }

        return [.. modes, AnalysisMode.Preview];
    }

    internal static bool CanSettleReanalysisRun(AnalysisMode mode, AnalyzerAction action, bool ffmpegValid)
    {
        if (mode != AnalysisMode.Introduction)
        {
            return true;
        }

        return ffmpegValid || action == AnalyzerAction.Chapter;
    }

    /// <summary>
    /// Returns <see langword="true"/> when at least one episode has no cached analysis result for
    /// the given mode (state <see cref="EpisodeState.NotAnalyzed"/>). Episodes already settled as
    /// <see cref="EpisodeState.NoSegments"/> are a negative-cache result for the current configuration
    /// and are intentionally not re-analyzed here; they are reconsidered only when the season is reset
    /// to <see cref="EpisodeState.NotAnalyzed"/> — e.g. a new episode is added, the analysis
    /// configuration (including Chromaprint availability) changes, or settled-season reanalysis runs.
    /// </summary>
    /// <param name="items">Episodes in the current season pass.</param>
    /// <param name="mode">Analysis mode being processed.</param>
    /// <returns><see langword="true"/> when analyzer execution should continue.</returns>
    internal static bool HasUncachedAnalysisWork(IReadOnlyList<QueuedEpisode> items, AnalysisMode mode)
    {
        return items.Any(e => e.GetAnalyzed(mode) == EpisodeState.NotAnalyzed);
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments. Every write into the
    /// segment store journals its item's projection, so the Jellyfin mirror converges
    /// from the journal — the pass itself never pushes.
    /// </summary>
    /// <param name="items">Media items to analyze.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">The season's analyzer action for the mode.</param>
    /// <param name="ffmpegValid">Whether FFmpeg supports the required Chromaprint features.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task AnalyzeItemsAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        CancellationToken cancellationToken)
    {
        if (!HasUncachedAnalysisWork(items, mode))
        {
            return;
        }

        var first = items[0];
        var category = first.Category;
        var isMovie = category == QueuedMediaCategory.Movie;
        var isAnime = category == QueuedMediaCategory.AnimeEpisode;

        if (AnalysisEligibility.IsSeasonZeroOptedOut(first, Config))
        {
            return;
        }

        var configHash = ConfigHasher.Analysis(Config, mode, action, ffmpegValid);

        if (action == AnalyzerAction.None)
        {
            LogSkippingNoneAction(_logger, mode, first.SeriesName, first.SeasonNumber);
            // The disabled action is part of the hash. Persist it as settled so the same season is not
            // queued and skipped forever on every subsequent run.
            await _database.MarkItemsAnalyzedAsync(
                mode,
                items.Select(i => i.EpisodeId),
                configHash,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var item in items)
        {
            item.AnalysisConfigHash = configHash;
        }

        // The cleanup journals the removed rows' projections, so they reach the
        // mirror even if the analyzers below detect nothing new.
        await _database.CleanStaleAutomaticSegmentsAsync(
            items.Where(e => e.GetAnalyzed(mode) != EpisodeState.UserProvided).Select(e => e.EpisodeId),
            mode,
            configHash,
            cancellationToken).ConfigureAwait(false);

        LogAnalyzingFiles(_logger, mode, items.Count, first.SeriesName, first.SeasonNumber);

        // Build the default analyzer chain for this mode and content type.
        // All applicable analyzers are always included — the order determines priority,
        // and each analyzer skips episodes already handled by earlier ones via NeedsAnalysis().
        var analyzers = new List<IMediaFileAnalyzer>
        {
            // ChapterAnalyzer: supports all modes and content types
            new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>(), _ffmpegService, _database, Config)
        };

        if (mode is AnalysisMode.Credits)
        {
            if (isAnime)
            {
                // Anime credits: Chromaprint before BlackFrame (fingerprint matching preferred)
                if (ffmpegValid)
                {
                    analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, Config));
                }

                analyzers.Add(CreateBlackFrameAnalyzer());
            }
            else
            {
                // Non-anime credits: BlackFrame before Chromaprint
                analyzers.Add(CreateBlackFrameAnalyzer());

                if (!isMovie && ffmpegValid)
                {
                    analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, Config));
                }
            }
        }
        else if (mode is AnalysisMode.Introduction)
        {
            // Introduction: Chromaprint is the only non-chapter analyzer
            if (!isMovie && ffmpegValid)
            {
                analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, Config));
            }
        }
        else if (mode is AnalysisMode.Recap && !isMovie && ffmpegValid)
        {
            // Recap: Chromaprint can match the repeated "previously on" card/sting near the start.
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, Config));
        }

        // Preview, Commercial: only ChapterAnalyzer (already added above)

        // Apply priority overrides to reorder the analyzer chain.
        // The specified analyzer moves to the front; others keep their relative order.
        // AnalyzerAction per-season override takes precedence over PreferChromaprint config.
        switch (action)
        {
            case AnalyzerAction.Chapter:
                PromoteAnalyzer(analyzers, static a => a is ChapterAnalyzer);
                break;
            case AnalyzerAction.Chromaprint:
                PromoteAnalyzer(analyzers, static a => a is ChromaprintAnalyzer);
                break;
            case AnalyzerAction.BlackFrame:
                PromoteAnalyzer(analyzers, static a => a is BlackFrameAnalyzer or CreditsBlackFrameAnalyzer);
                break;
            default:
                if (Config.PreferChromaprint && ffmpegValid)
                {
                    PromoteAnalyzer(analyzers, static a => a is ChromaprintAnalyzer);
                }

                break;
        }

        // Execute each analyzer in order. Analyzers skip episodes already
        // marked as analyzed by earlier ones via NeedsAnalysis().
        foreach (var analyzer in analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items = await analyzer.AnalyzeMediaFiles(items, mode, cancellationToken).ConfigureAwait(false);
        }

        // For anime, optionally create a Preview segment from the end of credits to the end of the episode.
        if (mode == AnalysisMode.Credits && isAnime && Config.AnimePreviewFromCreditsEnd)
        {
            await CreateAnimePreviewFromCreditsAsync(items, cancellationToken).ConfigureAwait(false);
        }

        // Record completed items under this hash, found segments or not. Failed items are omitted so
        // a transient FFmpeg or analyzer failure remains eligible on the next scan.
        await _database.MarkItemsAnalyzedAsync(
            mode,
            GetPersistableEpisodeIds(items, mode),
            configHash,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets episode IDs whose analysis result can be persisted. Transient failures remain without an
    /// analysis record so queue verification retries them on the next scan.
    /// </summary>
    /// <param name="items">The queued episodes whose analysis results were evaluated.</param>
    /// <param name="mode">The analysis mode used for the queued episodes.</param>
    /// <returns>The IDs of episodes with persistable analysis results.</returns>
    internal static IReadOnlyList<Guid> GetPersistableEpisodeIds(IReadOnlyList<QueuedEpisode> items, AnalysisMode mode)
        => [.. items
            .Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed)
            .Select(item => item.EpisodeId)];

    /// <summary>
    /// Decide whether an anime Preview segment needs to be written for an episode, and build it.
    /// </summary>
    /// <remarks>
    /// Returns a new Segment when the Preview is missing, its Start no longer matches the current
    /// credits.End (e.g. because settings changed and Credits was re-analyzed), or its End no longer
    /// matches the episode duration (e.g. because the underlying media file was replaced).
    /// Returns <see langword="null"/> when there are no valid credits, the credits already cover the
    /// episode, or any existing Preview already matches both the current credits.End and the episode
    /// duration within or equal to <see cref="AnimePreviewStartTolerance"/>.
    /// </remarks>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="episodeDuration">Episode duration in seconds.</param>
    /// <param name="credits">The credits segment feeding the preview (the latest-start credits block), or <see langword="null"/>.</param>
    /// <param name="existingPreviews">All current Preview segments of the episode.</param>
    /// <returns>Segment to write, or <see langword="null"/> when no write is needed.</returns>
    public static Segment? ComputeAnimePreviewFromCredits(
        Guid episodeId,
        double episodeDuration,
        Segment? credits,
        IReadOnlyCollection<Segment> existingPreviews)
    {
        ArgumentNullException.ThrowIfNull(existingPreviews);

        if (credits is null || !credits.Valid)
        {
            return null;
        }

        if (credits.End >= episodeDuration)
        {
            return null;
        }

        foreach (var existing in existingPreviews)
        {
            if (existing.Valid
                && Math.Abs(existing.Start - credits.End) <= AnimePreviewStartTolerance
                && Math.Abs(existing.End - episodeDuration) <= AnimePreviewStartTolerance)
            {
                return null;
            }
        }

        return new Segment(episodeId, new TimeRange(credits.End, episodeDuration));
    }

    /// <summary>
    /// Creates the configured black frame analyzer variant.
    /// </summary>
    /// <returns>A <see cref="CreditsBlackFrameAnalyzer"/> by default, or the legacy <see cref="BlackFrameAnalyzer"/> when configured.</returns>
    private IMediaFileAnalyzer CreateBlackFrameAnalyzer() => Config.UseLegacyBlackFrameAnalyzer
        ? new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>(), _ffmpegService, _database, Config)
        : new CreditsBlackFrameAnalyzer(_loggerFactory.CreateLogger<CreditsBlackFrameAnalyzer>(), _ffmpegService, _database, Config);

    /// <summary>
    /// Moves the first analyzer matching <paramref name="predicate"/> to the front of the list,
    /// preserving the relative order of all other analyzers.
    /// </summary>
    /// <param name="analyzers">The analyzer list to reorder in place.</param>
    /// <param name="predicate">Predicate identifying the analyzer to promote.</param>
    private static void PromoteAnalyzer(List<IMediaFileAnalyzer> analyzers, Func<IMediaFileAnalyzer, bool> predicate)
    {
        var index = analyzers.FindIndex(a => predicate(a));
        if (index > 0)
        {
            var analyzer = analyzers[index];
            analyzers.RemoveAt(index);
            analyzers.Insert(0, analyzer);
        }
    }

    /// <summary>
    /// For anime episodes, creates or refreshes a Preview segment covering the remaining content after the credits end.
    /// </summary>
    /// <param name="items">Media items to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task CreateAnimePreviewFromCreditsAsync(
        IReadOnlyList<QueuedEpisode> items,
        CancellationToken cancellationToken)
    {
        foreach (var episode in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var dbSegments = await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false);

            // A user-provided Preview settles the mode for the episode: the admission gate only
            // drops a derived preview that strictly overlaps it, so without this guard a
            // non-overlapping manual Preview would gain a second, automatic one beside it, and
            // the episode's UserProvided state would be overwritten with Analyzed below.
            if (dbSegments.Any(s => s.Type == AnalysisMode.Preview && s.Source == SegmentSource.User))
            {
                LogSkippedUserProvidedPreview(_logger, episode.Name);
                continue;
            }

            // The preview is the tail of the episode, so it follows the final credits block.
            var credits = dbSegments
                .Where(s => s.Type == AnalysisMode.Credits)
                .OrderBy(s => s.StartTicks)
                .LastOrDefault()?
                .ToSegment();
            var previews = dbSegments
                .Where(s => s.Type == AnalysisMode.Preview)
                .Select(s => s.ToSegment())
                .ToList();

            var preview = ComputeAnimePreviewFromCredits(episode.EpisodeId, episode.Duration, credits, previews);
            if (preview is null)
            {
                continue;
            }

            // The admission gate (AutoSegmentAdmissionPolicy) has the final say: an
            // overlapping tombstone drops the preview. Branch the log on the gate's outcome
            // so a dropped write is never reported as created. The episode still counts as
            // analyzed either way — re-running the analysis would not change the gate's answer.
            var stored = await _database.ReplaceAutoSegmentsAsync(episode.EpisodeId, AnalysisMode.Preview, [preview], SegmentSource.CreditsDerived, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
            episode.SetAnalyzed(AnalysisMode.Preview, EpisodeState.Analyzed);

            if (stored > 0)
            {
                LogCreatedAnimePreview(_logger, episode.Name, preview.Start, preview.End);
            }
            else
            {
                LogDroppedAnimePreview(_logger, episode.Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Created anime preview for {Episode}: {Start:F2}s to {End:F2}s")]
    private static partial void LogCreatedAnimePreview(ILogger logger, string episode, double start, double end);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping anime preview for {Episode}: a user-provided Preview already exists.")]
    private static partial void LogSkippedUserProvidedPreview(ILogger logger, string episode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Anime preview for {Episode} was dropped by the admission policy (overlapping user segment or tombstone).")]
    private static partial void LogDroppedAnimePreview(ILogger logger, string episode);

    [LoggerMessage(Level = LogLevel.Information, Message = "No libraries selected for analysis. To enable, check library configuration > Media Segment Providers.")]
    private static partial void LogNoLibrariesSelected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping Chromaprint analysis! Chromaprint is not enabled in the current ffmpeg. If Jellyfin is running natively, install jellyfin-ffmpeg7. If Jellyfin is running in a container, upgrade to version 10.10.0 or newer.")]
    private static partial void LogSkippingChromaprint(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-analyzing settled season {Season} of {Series} ({Count} episodes)")]
    private static partial void LogReanalyzingSettledSeason(ILogger logger, int season, string series, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis was canceled.")]
    private static partial void LogAnalysisCanceled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fingerprint exception during analysis.")]
    private static partial void LogFingerprintExceptionDuringAnalysis(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An ffmpeg scan timed out during analysis; skipping this season.")]
    private static partial void LogFfmpegTimeoutDuringAnalysis(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unexpected error occurred during analysis.")]
    private static partial void LogUnexpectedAnalysisError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}")]
    private static partial void LogAnalyzingFiles(ILogger logger, AnalysisMode mode, int count, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Skipping {Name} season {Season}: analyzer action is set to None")]
    private static partial void LogSkippingNoneAction(ILogger logger, AnalysisMode mode, string name, int season);
}
