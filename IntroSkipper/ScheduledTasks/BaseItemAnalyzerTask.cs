// SPDX-FileCopyrightText: 2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Analyzers;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Runs the analyzers over every resolved season, one mode at a time. Holds no per-run
/// state, so one instance serves every run.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BaseItemAnalyzerTask"/> class.
/// </remarks>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="seasonResolver">Resolver of library items into seasons.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="cacheDatabase">Detection cache database facade, for the per-item delete when a file was replaced.</param>
/// <param name="database">Segment database facade.</param>
public partial class BaseItemAnalyzerTask(
    ILoggerFactory loggerFactory,
    SeasonResolver seasonResolver,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    IDetectionCacheDatabase cacheDatabase,
    IIntroSkipperDatabase database)
{
    private static readonly AnalysisMode[] AllModes = Enum.GetValues<AnalysisMode>();

    private readonly ILogger _logger = loggerFactory.CreateLogger<BaseItemAnalyzerTask>();
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly IIntroSkipperDatabase _database = database;

    /// <summary>
    /// Gets the live plugin configuration. Jellyfin replaces the configuration object on save, so
    /// retaining a constructor-time snapshot can stamp analysis with a hash that no longer matches
    /// the analyzers or queue verifier.
    /// </summary>
    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Analyzes every season of every enabled library, or only the seasons that are, or
    /// contain, the given items.
    /// </summary>
    /// <param name="progress">Progress reporter, advanced as each series or movie is reached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="itemIds">Ids of the seasons, movies or episodes to analyze, or <see langword="null"/> for the whole library.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AnalyzeItemsAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? itemIds = null)
    {
        if (itemIds?.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var (modes, ffmpegValid) = await StartRunAsync(cancellationToken).ConfigureAwait(false);

        // A scoped run resolves only the series and movies owning the requested items. A
        // full run resolves every series and movie.
        var scope = itemIds?.ToHashSet();
        var owners = scope is null ? _seasonResolver.EnumerateLibrary() : _seasonResolver.OwnersOf(scope);
        if (owners.Count == 0 && scope is null)
        {
            LogNoLibrariesSelected(_logger);
            return;
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Config.MaxParallelism),
            CancellationToken = cancellationToken
        };

        // The seasons stream out of the owners one series at a time as the parallel slots
        // free up. A series' episodes are in memory only while its seasons run, and the
        // slots share the seasons of one large series.
        await Parallel.ForEachAsync(
            SeasonsInScope(owners, scope, progress),
            options,
            (season, ct) => new ValueTask(AnalyzeSeasonAsync(season, modes, ffmpegValid, ct))).ConfigureAwait(false);
        progress.Report(100);
    }

    // Resolves the owners in order, yielding the seasons in scope: every season, or those
    // keyed by or holding a requested item. Progress counts the owners reached.
    private IEnumerable<ResolvedSeason> SeasonsInScope(IReadOnlyList<BaseItem> owners, HashSet<Guid>? scope, IProgress<double> progress)
    {
        var yielded = 0;
        for (var i = 0; i < owners.Count; i++)
        {
            progress.Report(100.0 * i / owners.Count);
            IReadOnlyList<ResolvedSeason> seasons;
            try
            {
                seasons = _seasonResolver.Resolve(owners[i]);
            }
            catch (Exception ex)
            {
                LogFailedResolve(_logger, ex, owners[i].Name, owners[i].Id);
                continue;
            }

            foreach (var season in seasons)
            {
                if (scope is null || scope.Contains(season.Key) || season.Episodes.Any(episode => scope.Contains(episode.EpisodeId)))
                {
                    yielded++;
                    yield return season;
                }
            }
        }

        if (scope is not null && yielded == 0)
        {
            LogNothingInScope(_logger, scope.Count);
        }
    }

    /// <summary>
    /// Analyzes one season the caller resolved, so a scan erases and analyzes the same
    /// episodes. A season without episodes is logged and skipped.
    /// </summary>
    /// <param name="season">The resolved season.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task AnalyzeSeasonAsync(ResolvedSeason season, CancellationToken cancellationToken)
    {
        if (season.Episodes.Count == 0)
        {
            LogNothingToAnalyze(_logger, season.Key);
            return;
        }

        var (modes, ffmpegValid) = await StartRunAsync(cancellationToken).ConfigureAwait(false);
        await AnalyzeSeasonAsync(season, modes, ffmpegValid, cancellationToken).ConfigureAwait(false);
    }

    // The modes the configuration enables and whether ffmpeg supports chromaprint,
    // probed once per run.
    private async Task<(IReadOnlyList<AnalysisMode> Modes, bool FfmpegValid)> StartRunAsync(CancellationToken cancellationToken)
    {
        List<AnalysisMode> modes = [
            .. Config.ScanIntroduction ? [AnalysisMode.Introduction] : Array.Empty<AnalysisMode>(),
            .. Config.ScanCredits ? [AnalysisMode.Credits] : Array.Empty<AnalysisMode>(),
            .. Config.ScanRecap ? [AnalysisMode.Recap] : Array.Empty<AnalysisMode>(),
            .. Config.ScanPreview ? [AnalysisMode.Preview] : Array.Empty<AnalysisMode>(),
            .. Config.ScanCommercial ? [AnalysisMode.Commercial] : Array.Empty<AnalysisMode>()
        ];

        var ffmpegValid = await _ffmpegService.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false);
        if (!ffmpegValid)
        {
            LogSkippingChromaprint(_logger);
        }

        return (modes, ffmpegValid);
    }

    /// <summary>
    /// Verifies one season against the stored analysis state, reopens what a replaced file
    /// or a settled-season reanalysis invalidates, and runs every mode over it.
    /// </summary>
    private async Task AnalyzeSeasonAsync(ResolvedSeason season, IReadOnlyList<AnalysisMode> modes, bool ffmpegValid, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnalysisMode> settledResetModes = [];

        var episodes = await VerifyQueueAsync(season.Episodes, modes, ffmpegValid, cancellationToken).ConfigureAwait(false);
        if (episodes.Count == 0)
        {
            return;
        }

        var first = episodes[0];

        // A replaced file makes the old automatic segments and fingerprints wrong for every
        // mode, not only the ones this run covers. The fingerprints go first: the records
        // are the only evidence the file changed, so if the reset does not commit the
        // mismatch persists and the next run retries both. The reset journals its
        // deletions' projections. Every mode of the episode then reopens in memory too,
        // or a mode whose record matched would keep its settled state after its segments
        // were deleted and be recorded again with nothing behind it.
        var changedFiles = episodes.Where(e => e.FileChanged).Select(e => e.EpisodeId).ToArray();
        if (changedFiles.Length > 0)
        {
            await _cacheDatabase.DeleteForItemsAsync(changedFiles, cancellationToken).ConfigureAwait(false);
            await _database.ResetItemsForReanalysisAsync(changedFiles, AllModes, cancellationToken).ConfigureAwait(false);
            foreach (var episode in episodes.Where(e => e.FileChanged))
            {
                foreach (var mode in modes)
                {
                    if (episode.GetAnalyzed(mode) != EpisodeState.UserProvided)
                    {
                        episode.SetAnalyzed(mode, EpisodeState.NotAnalyzed);
                    }
                }
            }
        }

        // Run settled-season reanalysis from scratch after no new episodes have been added
        // for the configured delay so segments first derived from a partial season are
        // recomputed against the full season.
        // Reuses the cached fingerprints, so this only re-runs the comparison, not the decode.
        var utcNow = DateTime.UtcNow;
        var episodeIds = episodes.Select(e => e.EpisodeId).ToArray();

        // One season-state read serves both the settle decision and every mode's
        // analyzer action below.
        var seasonStates = await _database.GetSettleReanalysisStatesAsync(first.SeasonId, cancellationToken).ConfigureAwait(false);
        if (SeasonReanalysisPlanner.IsSettledForReanalysis(episodes, Config, utcNow))
        {
            settledResetModes = SeasonReanalysisPlanner.GetSettleReanalysisModes(seasonStates, episodeIds, modes, ffmpegValid);
            if (settledResetModes.Count > 0)
            {
                var resetModes = SeasonReanalysisPlanner.ExpandSettledResetModesForDerivedSegments(settledResetModes, Config.AnimePreviewFromCreditsEnd);
                LogReanalyzingSettledSeason(_logger, first.SeasonNumber, first.SeriesName, episodes.Count);

                // The reset journals its deletions' projections, so they propagate
                // to Jellyfin even if the recompute finds nothing.
                await _database.ResetItemsForReanalysisAsync(episodeIds, resetModes, cancellationToken).ConfigureAwait(false);
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
                cancellationToken.ThrowIfCancellationRequested();
                await AnalyzeItemsAsync(
                    episodes,
                    mode,
                    seasonStates.TryGetValue(mode, out var seasonState) ? seasonState.Action : AnalyzerAction.Default,
                    ffmpegValid,
                    cancellationToken).ConfigureAwait(false);

                // Record only the modes we independently selected for reanalysis. A derived mode added
                // by ExpandSettledResetModesForDerivedSegments (Preview from Credits) is reset and then
                // regenerated as a side effect of its source mode's analysis, so its completion rides on
                // the source mode's record and is intentionally not tracked separately here.
                if (settledResetModes.Contains(mode))
                {
                    completedSettledModes.Add(mode);
                }
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
            await _database.RecordSettleReanalysisAsync(first.SeasonId, completedSettledModes, episodeIds, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies that a season's resolved episodes are still in the library and on disk and
    /// are not excluded, then classifies each against the stored analysis state. Fetches
    /// each episode from the server again, because a season can wait behind the rest of
    /// its series after resolution, and one removed or re-imported meanwhile must not be
    /// analyzed under a stale id, path or file version.
    /// </summary>
    /// <param name="candidates">One season's resolved episodes.</param>
    /// <param name="modes">Analysis modes of the run.</param>
    /// <param name="ffmpegValid">Whether ffmpeg supports chromaprint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes that exist and are not excluded, classified per mode.</returns>
    internal async Task<IReadOnlyList<QueuedEpisode>> VerifyQueueAsync(IReadOnlyList<QueuedEpisode> candidates, IReadOnlyCollection<AnalysisMode> modes, bool ffmpegValid, CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var verified = new List<QueuedEpisode>(candidates.Count);
        var config = Config;

        // Built from the live configuration, not the resolution-time policy: exclusions
        // saved between resolution and this verification must apply.
        var policy = ExclusionPolicy.FromConfiguration(config);
        var snapshot = await _database.GetSeasonQueueSnapshotAsync(candidates[0].SeasonId, [.. candidates.Select(c => c.EpisodeId)], cancellationToken).ConfigureAwait(false);
        if (await LegacyAnalysisCompatibility.UpgradeAsync(_database, snapshot, config, cancellationToken).ConfigureAwait(false))
        {
            snapshot = await _database.GetSeasonQueueSnapshotAsync(candidates[0].SeasonId, [.. candidates.Select(c => c.EpisodeId)], cancellationToken).ConfigureAwait(false);
        }

        var verifier = new QueueVerifier(config, modes, snapshot, ffmpegValid);

        foreach (var candidate in candidates)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = Plugin.Instance!.GetItem(candidate.EpisodeId);
                if (item is not { Path: { Length: > 0 } path } || !File.Exists(path))
                {
                    LogSkippingFileNotFound(_logger, candidate.Name, candidate.EpisodeId);
                    continue;
                }

                var decision = candidate.Category == QueuedMediaCategory.Movie
                    ? policy.EvaluateMovie(candidate.Name, path)
                    : policy.EvaluateSeries(candidate.SeriesName, path);
                if (decision.IsExcluded)
                {
                    LogSkippingExcludedItem(_logger, candidate.Name, decision.RuleLabel);
                    continue;
                }

                candidate.Path = path;
                candidate.FileVersion = SeasonResolver.FileVersion(item);
                verified.Add(candidate);
                verifier.Classify(candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogSkippingAnalysisException(_logger, candidate.Name, candidate.EpisodeId, ex);
            }
        }

        verifier.LogAnalysisReasons(_logger, verified);
        await _database.BackfillFileVersionsAsync(verifier.FileVersionBackfill, cancellationToken).ConfigureAwait(false);

        return verified;
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
    internal async Task AnalyzeItemsAsync(
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
                items.Select(i => (i.EpisodeId, i.FileVersion)),
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

        if (mode == AnalysisMode.Credits)
        {
            await SetCreditsWindowsAsync(items, isMovie, cancellationToken).ConfigureAwait(false);

            // Credits settle chapter matches first by default; enhancement combines them
            // with other candidates. A single item cannot use chromaprint comparison.
            var pass = new CreditsPass(_loggerFactory, _ffmpegService, _cacheService, _database, Config);
            await pass.RunAsync(items, action, ffmpegValid, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RunAnalyzerChainAsync(items, mode, action, ffmpegValid, isMovie, cancellationToken).ConfigureAwait(false);
        }

        // Anime previews derive from the credits: right after a credits result lands, and again
        // in the Preview mode for episodes no preview analyzer settled, since a season whose
        // credits are all user-provided never enters the credits pass but its derived previews
        // still go stale when the preview settings change.
        if (isAnime && Config.AnimePreviewFromCreditsEnd)
        {
            if (mode == AnalysisMode.Credits)
            {
                await AnimePreviewDeriver.DeriveAsync(_database, items, Config.MinimumPreviewDuration, cancellationToken).ConfigureAwait(false);
            }
            else if (mode == AnalysisMode.Preview)
            {
                List<QueuedEpisode> unsettled = [.. items.Where(item => item.NeedsAnalysis(AnalysisMode.Preview))];
                await AnimePreviewDeriver.DeriveAsync(_database, unsettled, Config.MinimumPreviewDuration, cancellationToken).ConfigureAwait(false);
            }
        }

        // Record completed items under this hash, found segments or not. Failed items are omitted so
        // a transient FFmpeg or analyzer failure remains eligible on the next scan.
        await _database.MarkItemsAnalyzedAsync(
            mode,
            items.Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed).Select(item => (item.EpisodeId, item.FileVersion)),
            configHash,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the first-wins analyzer chain for the non-credits modes: every applicable analyzer
    /// runs in priority order and each skips the episodes an earlier one settled via
    /// <see cref="QueuedEpisode.NeedsAnalysis"/>.
    /// </summary>
    private async Task RunAnalyzerChainAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        bool isMovie,
        CancellationToken cancellationToken)
    {
        // Chapters come first. Chromaprint needs a season to compare (no movies) and a
        // compatible ffmpeg.
        var chapter = new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>(), _ffmpegService, _database, Config);
        IMediaFileAnalyzer? chromaprint = ffmpegValid && !isMovie && mode is AnalysisMode.Introduction or AnalysisMode.Recap
            ? new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, Config)
            : null;

        List<IMediaFileAnalyzer?> chain = [chapter, chromaprint];
        var analyzers = chain.OfType<IMediaFileAnalyzer>().ToList();

        // A per-season action, or the PreferChromaprint setting, moves one analyzer to the front;
        // the rest keep their relative order. An action naming an analyzer that is not in the
        // chain (Chromaprint without ffmpeg) changes nothing.
        var preferred = action switch
        {
            AnalyzerAction.Chapter => chapter,
            AnalyzerAction.Chromaprint => chromaprint,
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
    }

    /// <summary>
    /// Sets each episode's credits fingerprint window. Every episode of the season gets
    /// one, settled siblings included, because the chromaprint comparison reads their
    /// cached fingerprints under the same window. The audio duration is probed only here,
    /// so a run that settles a season without entering the credits pass spawns no ffprobe.
    /// </summary>
    private async Task SetCreditsWindowsAsync(IReadOnlyList<QueuedEpisode> items, bool isMovie, CancellationToken cancellationToken)
    {
        var config = Config;

        // Credits have their own maximum duration in seconds. The general analysis
        // percentage is not applied to them, since it can exclude the actual credits boundary.
        var maxCreditsDuration = isMovie ? config.MaximumMovieCreditsDuration : config.MaximumCreditsDuration;
        foreach (var item in items)
        {
            var creditsEnd = item.Duration;
            if (config.ProbeAudioDuration)
            {
                var audioDuration = await _ffmpegService.ProbeAudioDurationAsync(item.Path, cancellationToken).ConfigureAwait(false);
                if (audioDuration is > 0 && audioDuration.Value < item.Duration)
                {
                    creditsEnd = audioDuration.Value;
                }
            }

            item.CreditsFingerprintStart = Math.Max(0, creditsEnd - maxCreditsDuration);
            item.CreditsFingerprintEnd = creditsEnd;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No libraries selected for analysis. To enable, check library configuration > Media Segment Providers.")]
    private static partial void LogNoLibrariesSelected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve {Name} ({Id}); skipping it this run")]
    private static partial void LogFailedResolve(ILogger logger, Exception exception, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "None of the {Count} requested items resolved to a season to analyze")]
    private static partial void LogNothingInScope(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Season {SeasonId} has no episodes to analyze")]
    private static partial void LogNothingToAnalyze(ILogger logger, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping {Name} ({Id}): file not found")]
    private static partial void LogSkippingFileNotFound(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded item {Name}: matched {RuleLabel}")]
    private static partial void LogSkippingExcludedItem(ILogger logger, string name, string ruleLabel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping analysis of {Name} ({Id})")]
    private static partial void LogSkippingAnalysisException(ILogger logger, string name, Guid id, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping Chromaprint analysis! Chromaprint is not enabled in the current ffmpeg. If Jellyfin is running natively, install jellyfin-ffmpeg7. If Jellyfin is running in a container, upgrade to version 10.10.0 or newer.")]
    private static partial void LogSkippingChromaprint(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-analyzing settled season {Season} of {Series} ({Count} episodes)")]
    private static partial void LogReanalyzingSettledSeason(ILogger logger, int season, string series, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fingerprint exception during analysis.")]
    private static partial void LogFingerprintExceptionDuringAnalysis(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An ffmpeg scan timed out during analysis; skipping this season. Raise the FFmpeg scan timeout in the plugin settings if this repeats.")]
    private static partial void LogFfmpegTimeoutDuringAnalysis(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}")]
    private static partial void LogAnalyzingFiles(ILogger logger, AnalysisMode mode, int count, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Skipping {Name} season {Season}: analyzer action is set to None")]
    private static partial void LogSkippingNoneAction(ILogger logger, AnalysisMode mode, string name, int season);
}
