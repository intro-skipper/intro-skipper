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
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Runs the analyzers over every queued season, one mode at a time.
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
                settledResetModes = SeasonReanalysisPlanner.GetSettleReanalysisModes(seasonStates, episodeIds, modes, ffmpegValid);
                if (settledResetModes.Count > 0)
                {
                    var resetModes = SeasonReanalysisPlanner.ExpandSettledResetModesForDerivedSegments(settledResetModes, Config.AnimePreviewFromCreditsEnd);
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
            catch (FingerprintException ex)
            {
                LogFingerprintExceptionDuringAnalysis(_logger, ex);
            }
            catch (TimeoutException ex)
            {
                LogFfmpegTimeoutDuringAnalysis(_logger, ex);
            }

            // No mirror push here: every write above journaled its item's projection
            // with the change, and the projection worker converges Jellyfin durably.
            if (completedSettledModes.Count > 0)
            {
                await _database.RecordSettleReanalysisAsync(first.SeasonId, completedSettledModes, episodeIds, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments. Every write into the
    /// segment store journals its item's projection, so the Jellyfin mirror converges
    /// from the journal; the pass itself never pushes.
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
        // NoSegments is a negative-cache result for the current configuration; only an episode
        // reset to NotAnalyzed (new episode, configuration or Chromaprint-availability change,
        // settled-season reanalysis) reopens the season, and then NeedsAnalysis() gives the
        // settled episodes another chance too.
        if (!items.Any(e => e.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
        {
            return;
        }

        var first = items[0];
        var isMovie = first.Category == QueuedMediaCategory.Movie;
        var isAnime = first.Category == QueuedMediaCategory.AnimeEpisode;

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

        // Every applicable analyzer runs; the order is the priority, and each analyzer skips
        // episodes an earlier one already settled via NeedsAnalysis(). Chapters come first.
        // Chromaprint needs a season to compare (no movies) and a compatible ffmpeg; black
        // frames only find credits. Anime credits prefer the fingerprint match over black frames.
        var chapter = new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>(), _ffmpegService, _database, Config);
        IMediaFileAnalyzer? chromaprint = ffmpegValid && !isMovie && mode is AnalysisMode.Introduction or AnalysisMode.Credits or AnalysisMode.Recap
            ? new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, Config)
            : null;
        IMediaFileAnalyzer? blackFrame = mode == AnalysisMode.Credits ? CreateBlackFrameAnalyzer() : null;

        List<IMediaFileAnalyzer?> chain = isAnime ? [chapter, chromaprint, blackFrame] : [chapter, blackFrame, chromaprint];
        var analyzers = chain.OfType<IMediaFileAnalyzer>().ToList();

        // A per-season action, or the PreferChromaprint setting, moves one analyzer to the front;
        // the rest keep their relative order. An action naming an analyzer that is not in the
        // chain (BlackFrame outside Credits, Chromaprint without ffmpeg) changes nothing.
        var preferred = action switch
        {
            AnalyzerAction.Chapter => chapter,
            AnalyzerAction.Chromaprint => chromaprint,
            AnalyzerAction.BlackFrame => blackFrame,
            _ => Config.PreferChromaprint && ffmpegValid ? chromaprint : null,
        };
        if (preferred is not null && analyzers.Remove(preferred))
        {
            analyzers.Insert(0, preferred);
        }

        foreach (var analyzer in analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items = await analyzer.AnalyzeMediaFiles(items, mode, cancellationToken).ConfigureAwait(false);
        }

        if (mode == AnalysisMode.Credits && isAnime && Config.AnimePreviewFromCreditsEnd)
        {
            await AnimePreviewDeriver.DeriveAsync(_database, items, cancellationToken).ConfigureAwait(false);
        }

        // Record completed items under this hash, found segments or not. Failed items are omitted so
        // a transient FFmpeg or analyzer failure remains eligible on the next scan.
        await _database.MarkItemsAnalyzedAsync(
            mode,
            items.Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed).Select(item => item.EpisodeId),
            configHash,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the configured black frame analyzer variant.
    /// </summary>
    /// <returns>A <see cref="CreditsBlackFrameAnalyzer"/> by default, or the legacy <see cref="BlackFrameAnalyzer"/> when configured.</returns>
    private IMediaFileAnalyzer CreateBlackFrameAnalyzer() => Config.UseLegacyBlackFrameAnalyzer
        ? new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>(), _ffmpegService, _database, Config)
        : new CreditsBlackFrameAnalyzer(_loggerFactory.CreateLogger<CreditsBlackFrameAnalyzer>(), _ffmpegService, _database, Config);

    [LoggerMessage(Level = LogLevel.Information, Message = "No libraries selected for analysis. To enable, check library configuration > Media Segment Providers.")]
    private static partial void LogNoLibrariesSelected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping Chromaprint analysis! Chromaprint is not enabled in the current ffmpeg. If Jellyfin is running natively, install jellyfin-ffmpeg7. If Jellyfin is running in a container, upgrade to version 10.10.0 or newer.")]
    private static partial void LogSkippingChromaprint(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-analyzing settled season {Season} of {Series} ({Count} episodes)")]
    private static partial void LogReanalyzingSettledSeason(ILogger logger, int season, string series, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fingerprint exception during analysis.")]
    private static partial void LogFingerprintExceptionDuringAnalysis(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An ffmpeg scan timed out during analysis; skipping this season.")]
    private static partial void LogFfmpegTimeoutDuringAnalysis(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}")]
    private static partial void LogAnalyzingFiles(ILogger logger, AnalysisMode mode, int count, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Skipping {Name} season {Season}: analyzer action is set to None")]
    private static partial void LogSkippingNoneAction(ILogger logger, AnalysisMode mode, string name, int season);
}
