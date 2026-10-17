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
using IntroSkipper.Remote;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;
using SegmentScanner.Contracts;

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
/// <param name="remoteAnalysisDispatcher">Optional remote analysis dispatcher.</param>
public partial class BaseItemAnalyzerTask(
    ILoggerFactory loggerFactory,
    SeasonResolver seasonResolver,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    DetectionCacheDatabase cacheDatabase,
    IntroSkipperDatabase database,
    IRemoteAnalysisDispatcher? remoteAnalysisDispatcher = null)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<BaseItemAnalyzerTask>();
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly DetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly IntroSkipperDatabase _database = database;
    private readonly IRemoteAnalysisDispatcher? _remoteAnalysisDispatcher = remoteAnalysisDispatcher;

    /// <summary>
    /// Gets the live plugin configuration. Jellyfin replaces the configuration object on save, so
    /// retaining a constructor-time snapshot can stamp analysis with a hash that no longer matches
    /// the analyzers or queue verifier.
    /// </summary>
    private PluginConfiguration Config => AnalysisEnvironment?.Configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets explicit inputs for portable analysis; the Jellyfin host uses its live plugin environment.
    /// </summary>
    internal IAnalysisEnvironment? AnalysisEnvironment { get; init; }

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

        // A scoped run resolves only the series and movies owning the requested items. A
        // full run resolves every series and movie.
        var scope = itemIds?.ToHashSet();
        if (scope is null)
        {
            WarningManager.ResetFingerprintFailures();
        }

        var owners = scope is null ? _seasonResolver.EnumerateLibrary(out _) : _seasonResolver.OwnersOf(scope);
        if (owners.Count == 0 && scope is null)
        {
            LogNoLibrariesSelected(_logger);
            return;
        }

        var catalogLibraryIds = owners.Select(owner => owner.ParentId).Where(id => id != Guid.Empty).Distinct().ToArray();
        var localNode = _remoteAnalysisDispatcher is null
            ? null
            : await _database.GetLocalNodeAsync(catalogLibraryIds, cancellationToken).ConfigureAwait(false);
        var (modes, ffmpegValid) = await StartRunAsync(localNode?.Enabled ?? true, cancellationToken).ConfigureAwait(false);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = localNode is null ? Math.Max(1, Config.MaxParallelism) : Math.Max(1, localNode.Policy?.ConcurrentFiles ?? 1),
            CancellationToken = cancellationToken
        };

        // The seasons stream out of the owners one series at a time as the parallel slots
        // free up. A series' episodes are in memory only while its seasons run, and the
        // slots share the seasons of one large series.
        await Parallel.ForEachAsync(
            SeasonsInScope(owners, scope, progress),
            options,
            (season, ct) => new ValueTask(TryAnalyzeSeasonAsync(season, modes, ffmpegValid, localNode, ct))).ConfigureAwait(false);
        progress.Report(100);
    }

    // A season that throws is logged and skipped so the pass goes on to the others: a
    // changed-items pass at the end of a library scan covers every item Jellyfin saved,
    // and one broken season must not cost the rest their analysis. Cancellation still
    // stops the pass.
    private async Task TryAnalyzeSeasonAsync(ResolvedSeason season, IReadOnlyList<AnalysisMode> modes, bool ffmpegValid, RemoteNodeInfo? localNode, CancellationToken cancellationToken)
    {
        try
        {
            await AnalyzeSeasonAsync(season, modes, ffmpegValid, localNode, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var first = season.Episodes.Count > 0 ? season.Episodes[0] : null;
            LogSeasonFailed(_logger, ex, first?.SeriesName ?? string.Empty, first?.SeasonNumber ?? 0, season.Key);
        }
    }

    // Resolves the owners in order, yielding the seasons in scope: every season, or those
    // keyed by or holding a requested item. Progress counts the owners reached.
    private IEnumerable<ResolvedSeason> SeasonsInScope(IReadOnlyList<BaseItem> owners, HashSet<Guid>? scope, IProgress<double> progress)
    {
        var yielded = 0;
        for (var i = 0; i < owners.Count; i++)
        {
            progress.Report(100.0 * i / owners.Count);
            if (!_seasonResolver.TryResolve(owners[i], includeExcluded: false, out var seasons))
            {
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

    // Managed runs consider every mode; each node policy decides ownership. Legacy
    // dispatcher-free runs retain the configuration flags.
    private async Task<(IReadOnlyList<AnalysisMode> Modes, bool FfmpegValid)> StartRunAsync(bool localEnabled, CancellationToken cancellationToken)
    {
        List<AnalysisMode> modes = _remoteAnalysisDispatcher is not null
            ? [AnalysisMode.Introduction, AnalysisMode.Credits, AnalysisMode.Recap, AnalysisMode.Preview, AnalysisMode.Commercial]
            : [
            .. Config.ScanIntroduction ? [AnalysisMode.Introduction] : Array.Empty<AnalysisMode>(),
            .. Config.ScanCredits ? [AnalysisMode.Credits] : Array.Empty<AnalysisMode>(),
            .. Config.ScanRecap ? [AnalysisMode.Recap] : Array.Empty<AnalysisMode>(),
            .. Config.ScanPreview ? [AnalysisMode.Preview] : Array.Empty<AnalysisMode>(),
            .. Config.ScanCommercial ? [AnalysisMode.Commercial] : Array.Empty<AnalysisMode>()
        ];

        var ffmpegValid = localEnabled
            ? await _ffmpegService.CheckFFmpegVersionAsync(cancellationToken).ConfigureAwait(false)
            : true;
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
    private async Task AnalyzeSeasonAsync(ResolvedSeason season, IReadOnlyList<AnalysisMode> modes, bool ffmpegValid, RemoteNodeInfo? localNode, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnalysisMode> settledResetModes = [];

        var overrides = await _database.GetAnalysisOverridesAsync(season.Key, cancellationToken).ConfigureAwait(false);
        foreach (var episode in season.Episodes)
        {
            episode.AnalysisPercentOverride = overrides.AnalysisPercent;
            episode.AnalysisLengthLimitOverride = overrides.AnalysisLengthLimit;
            episode.PreviewFromCreditsEndOverride = overrides.PreviewFromCreditsEnd;
            episode.SubtitleRecapDetectionOverride = overrides.SubtitleRecapDetection;
            episode.SubtitlePreviewDetectionOverride = overrides.SubtitlePreviewDetection;

            var config = Config;
            var duration = episode.Duration;
            var analysisPercent = (overrides.AnalysisPercent ?? config.AnalysisPercent) / 100.0;
            var analysisLengthLimit = overrides.AnalysisLengthLimit ?? config.AnalysisLengthLimit;
            episode.IntroFingerprintEnd = Math.Min(
                duration >= 5 * 60 ? duration * analysisPercent : duration,
                60 * analysisLengthLimit);
        }

        var episodes = await VerifyQueueAsync(season.Episodes, modes, ffmpegValid, cancellationToken).ConfigureAwait(false);
        if (episodes.Count == 0)
        {
            return;
        }

        var first = episodes[0];
        var previewFromCreditsEnd = ShouldDerivePreview(first, Config);
        var seasonStates = await _database.GetSettleReanalysisStatesAsync(first.SeasonId, cancellationToken).ConfigureAwait(false);

        // A replaced file makes the old automatic segments and fingerprints wrong for every
        // mode, not only the ones this run covers. The fingerprints go first: the records
        // are the only evidence the file changed, so if the reset does not commit the
        // mismatch persists and the next run retries both. The reset journals its
        // deletions' projections. Each locally assigned mode then reopens in memory too,
        // or a mode whose record matched would keep its settled state after its segments
        // were deleted and be recorded again with nothing behind it.
        var changedFiles = episodes
            .Where(episode => episode.FileChanged && modes.Any(mode => IsLocallyEligible(localNode, episode, mode)))
            .Select(episode => episode.EpisodeId)
            .ToArray();
        if (changedFiles.Length > 0)
        {
            await _cacheDatabase.DeleteForItemsAsync(changedFiles, cancellationToken).ConfigureAwait(false);
            foreach (var mode in modes)
            {
                var localChangedFiles = episodes
                    .Where(episode => episode.FileChanged && IsLocallyEligible(localNode, episode, mode))
                    .Select(episode => episode.EpisodeId)
                    .ToArray();
                if (localChangedFiles.Length == 0)
                {
                    continue;
                }

                await _database.ResetItemsForReanalysisAsync(
                    localChangedFiles,
                    [mode],
                    GetSubtitleModesToPreserve([mode], seasonStates, episodes),
                    cancellationToken).ConfigureAwait(false);
                foreach (var episode in episodes.Where(episode => localChangedFiles.Contains(episode.EpisodeId)))
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
        // Reuses the cached fingerprints and scans, so this re-runs the comparison; only the credits
        // lead-in probe decodes its window again.
        var utcNow = DateTime.UtcNow;
        var episodeIds = episodes.Select(e => e.EpisodeId).ToArray();

        if (!previewFromCreditsEnd)
        {
            var localPreviewIds = episodes
                .Where(episode => IsLocallyEligible(localNode, episode, AnalysisMode.Preview))
                .Select(episode => episode.EpisodeId)
                .ToArray();
            if (localPreviewIds.Length > 0)
            {
                await _database.ClearCreditsDerivedPreviewsAsync(localPreviewIds, cancellationToken).ConfigureAwait(false);
            }
        }

        // The season-state snapshot serves the settle decision and every mode's analyzer action.
        if (SeasonReanalysisPlanner.IsSettledForReanalysis(episodes, Config, utcNow))
        {
            settledResetModes = SeasonReanalysisPlanner.GetSettleReanalysisModes(seasonStates, episodeIds, modes, ffmpegValid);
            if (settledResetModes.Count > 0)
            {
                var resetModes = SeasonReanalysisPlanner.ExpandSettledResetModesForDerivedSegments(settledResetModes, previewFromCreditsEnd);
                LogReanalyzingSettledSeason(_logger, first.SeasonNumber, first.SeriesName, episodes.Count);

                // The reset journals its deletions' projections, so they propagate
                // to Jellyfin even if the recompute finds nothing.
                foreach (var resetMode in resetModes)
                {
                    var localResetIds = episodes
                        .Where(episode => IsLocallyEligible(localNode, episode, resetMode))
                        .Select(episode => episode.EpisodeId)
                        .ToArray();
                    if (localResetIds.Length == 0)
                    {
                        continue;
                    }

                    await _database.ResetItemsForReanalysisAsync(
                        localResetIds,
                        [resetMode],
                        GetSubtitleModesToPreserve([resetMode], seasonStates, episodes),
                        cancellationToken).ConfigureAwait(false);
                    foreach (var episode in episodes.Where(episode => localResetIds.Contains(episode.EpisodeId)))
                    {
                        if (episode.GetAnalyzed(resetMode) != EpisodeState.UserProvided)
                        {
                            episode.SetAnalyzed(resetMode, EpisodeState.NotAnalyzed);
                            episode.ClearSubtitleOnlyReanalysis(resetMode);
                        }
                    }
                }
            }
        }

        var handledChaptersByMode = new Dictionary<AnalysisMode, IReadOnlySet<Guid>>();
        if (modes.Contains(AnalysisMode.Credits))
        {
            var creditsAction = seasonStates.TryGetValue(AnalysisMode.Credits, out var creditsState) ? creditsState.Action : AnalyzerAction.Default;
            var handledChapters = await SettleLocalCreditsChaptersAsync(episodes, creditsAction, ffmpegValid, localNode, cancellationToken).ConfigureAwait(false);
            if (handledChapters is not null)
            {
                handledChaptersByMode[AnalysisMode.Credits] = handledChapters;
            }
        }

        var remoteOwnedByMode = new Dictionary<AnalysisMode, IReadOnlySet<Guid>>(modes.Count);
        foreach (var mode in modes)
        {
            var action = seasonStates.TryGetValue(mode, out var seasonState) ? seasonState.Action : AnalyzerAction.Default;
            remoteOwnedByMode[mode] = await QueueRemoteAnalysisAsync(episodes, mode, action, ffmpegValid, cancellationToken).ConfigureAwait(false);
        }

        var completedSettledModes = new List<AnalysisMode>(settledResetModes.Count);

        try
        {
            foreach (var mode in modes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var locallyHandled = await AnalyzeItemsLocallyAsync(
                    episodes,
                    mode,
                    seasonStates.TryGetValue(mode, out var seasonState) ? seasonState.Action : AnalyzerAction.Default,
                    ffmpegValid,
                    localNode,
                    remoteOwnedByMode[mode],
                    remoteOwnedByMode,
                    handledChaptersByMode.GetValueOrDefault(mode),
                    cancellationToken).ConfigureAwait(false);

                // Record only the modes we independently selected for reanalysis. A derived mode added
                // by ExpandSettledResetModesForDerivedSegments (Preview from Credits) is reset and then
                // regenerated as a side effect of its source mode's analysis, so its completion rides on
                // the source mode's record and is intentionally not tracked separately here.
                if (locallyHandled && settledResetModes.Contains(mode))
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
        var legacyExcludedModes = GetLegacyHashAdoptionExclusions(candidates[0], config);
        if (await LegacyAnalysisCompatibility.UpgradeAsync(
                _database,
                snapshot,
                config,
                legacyExcludedModes,
                cancellationToken).ConfigureAwait(false))
        {
            snapshot = await _database.GetSeasonQueueSnapshotAsync(candidates[0].SeasonId, [.. candidates.Select(c => c.EpisodeId)], cancellationToken).ConfigureAwait(false);
        }

        var previewFromCreditsEnd = ShouldDerivePreview(candidates[0], config);
        var verifier = new QueueVerifier(
            config,
            modes,
            snapshot,
            ffmpegValid,
            candidates[0].AnalysisPercentOverride,
            candidates[0].AnalysisLengthLimitOverride,
            previewFromCreditsEnd,
            candidates[0].SubtitleRecapDetectionOverride,
            candidates[0].SubtitlePreviewDetectionOverride);

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

    private static IReadOnlySet<AnalysisMode> GetLegacyHashAdoptionExclusions(QueuedEpisode episode, PluginConfiguration config)
    {
        var excludedModes = new HashSet<AnalysisMode>();
        if (episode.AnalysisPercentOverride is { } analysisPercent && analysisPercent != config.AnalysisPercent)
        {
            excludedModes.Add(AnalysisMode.Introduction);
            excludedModes.Add(AnalysisMode.Recap);
        }

        if (episode.AnalysisLengthLimitOverride is { } analysisLengthLimit && analysisLengthLimit != config.AnalysisLengthLimit)
        {
            excludedModes.Add(AnalysisMode.Introduction);
            excludedModes.Add(AnalysisMode.Recap);
        }

        if (episode.PreviewFromCreditsEndOverride is { } previewFromCreditsEnd
            && previewFromCreditsEnd != config.AnimePreviewFromCreditsEnd)
        {
            excludedModes.Add(AnalysisMode.Credits);
            excludedModes.Add(AnalysisMode.Preview);
        }

        if (episode.SubtitleRecapDetectionOverride is { } subtitleRecapDetection
            && subtitleRecapDetection != config.EnableSubtitleRecapDetection)
        {
            excludedModes.Add(AnalysisMode.Recap);
        }

        if (episode.SubtitlePreviewDetectionOverride is { } subtitlePreviewDetection
            && subtitlePreviewDetection != config.EnableSubtitlePreviewDetection)
        {
            excludedModes.Add(AnalysisMode.Preview);
        }

        return excludedModes;
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
        var localNode = _remoteAnalysisDispatcher is null
            ? null
            : await _database.GetLocalNodeAsync([], cancellationToken).ConfigureAwait(false);
        var handledChapters = mode == AnalysisMode.Credits
            ? await SettleLocalCreditsChaptersAsync(items, action, ffmpegValid, localNode, cancellationToken).ConfigureAwait(false)
            : null;
        var remoteOwned = await QueueRemoteAnalysisAsync(items, mode, action, ffmpegValid, cancellationToken).ConfigureAwait(false);
        await AnalyzeItemsLocallyAsync(items, mode, action, ffmpegValid, localNode, remoteOwned, null, handledChapters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlySet<Guid>?> SettleLocalCreditsChaptersAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalyzerAction action,
        bool ffmpegValid,
        RemoteNodeInfo? localNode,
        CancellationToken cancellationToken)
    {
        if (action == AnalyzerAction.None)
        {
            return null;
        }

        var localCredits = items.Where(item => IsLocallyEligible(localNode, item, AnalysisMode.Credits)).ToArray();
        if (localCredits.Length == 0)
        {
            return null;
        }

        var creditsPass = new CreditsPass(_loggerFactory, _ffmpegService, _cacheService, _database, Config, AnalysisEnvironment ?? new PluginAnalysisEnvironment());
        return await creditsPass.SettleChaptersAsync(localCredits, action, ffmpegValid, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlySet<Guid>> QueueRemoteAnalysisAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        CancellationToken cancellationToken)
    {
        if (_remoteAnalysisDispatcher is null || action == AnalyzerAction.None || items.Count == 0 || AnalysisEligibility.IsSeasonZeroOptedOut(items[0], Config))
        {
            return new HashSet<Guid>();
        }

        var configHash = GetAnalysisConfigHash(items[0], mode, action, ffmpegValid);
        foreach (var item in items)
        {
            item.AnalysisConfigHash = configHash;
        }

        var writeTargets = items.Where(item => item.NeedsAnalysis(mode)).ToArray();
        var writeTargetIds = writeTargets.Select(item => item.EpisodeId).ToHashSet();
        return await _remoteAnalysisDispatcher
            .QueueAsync(writeTargets, items.Where(item => !writeTargetIds.Contains(item.EpisodeId)).ToArray(), mode, cancellationToken, action)
            .ConfigureAwait(false);
    }

    private bool IsLocallyEligible(RemoteNodeInfo? localNode, QueuedEpisode item, AnalysisMode mode)
    {
        if (_remoteAnalysisDispatcher is null)
        {
            return true;
        }

        if (localNode is not { Enabled: true, Policy: { } policy } || !policy.Modes.Contains(RemoteModes.ToRemote(mode)))
        {
            return false;
        }

        if (localNode.Revision == 0)
        {
            return true;
        }

        var owner = Plugin.Instance?.GetItem(item.SeriesId == Guid.Empty ? item.EpisodeId : item.SeriesId);
        return owner is not null && Plugin.Instance!.IsInLibrary(owner, policy.LibraryIds);
    }

    private static bool IsRemotelyOwned(
        IReadOnlyDictionary<AnalysisMode, IReadOnlySet<Guid>>? remoteOwnedByMode,
        AnalysisMode mode,
        Guid episodeId)
        => remoteOwnedByMode is not null
            && remoteOwnedByMode.TryGetValue(mode, out var remoteOwned)
            && remoteOwned.Contains(episodeId);

    private string GetAnalysisConfigHash(QueuedEpisode first, AnalysisMode mode, AnalyzerAction action, bool ffmpegValid)
        => ConfigHasher.Analysis(
            Config,
            mode,
            action,
            ffmpegValid,
            first.AnalysisPercentOverride,
            first.AnalysisLengthLimitOverride,
            ShouldDerivePreview(first, Config),
            IsSubtitleDetectionEnabled(first, AnalysisMode.Recap, Config),
            IsSubtitleDetectionEnabled(first, AnalysisMode.Preview, Config));

    private async Task<bool> AnalyzeItemsLocallyAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        RemoteNodeInfo? localNode,
        IReadOnlySet<Guid> remoteOwnedIds,
        IReadOnlyDictionary<AnalysisMode, IReadOnlySet<Guid>>? remoteOwnedByMode,
        IReadOnlySet<Guid>? handledChapters,
        CancellationToken cancellationToken)
    {
        var localItems = items.Where(item => IsLocallyEligible(localNode, item, mode) && !remoteOwnedIds.Contains(item.EpisodeId)).ToArray();

        // NoSegments is a negative-cache result for the current configuration; only an episode
        // reset to NotAnalyzed (new episode, configuration or Chromaprint-availability change,
        // settled-season reanalysis) reopens the season, and then NeedsAnalysis() gives the
        // settled episodes another chance too.
        if (!localItems.Any(e => e.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
        {
            return false;
        }

        var first = localItems[0];
        var isMovie = first.Category == QueuedMediaCategory.Movie;

        if (AnalysisEligibility.IsSeasonZeroOptedOut(first, Config))
        {
            return false;
        }

        var configHash = GetAnalysisConfigHash(first, mode, action, ffmpegValid);

        if (action == AnalyzerAction.None)
        {
            LogSkippingNoneAction(_logger, mode, first.SeriesName, first.SeasonNumber);
            // The disabled action is part of the hash. Persist it as settled so the same season is not
            // queued and skipped forever on every subsequent run.
            await _database.MarkItemsAnalyzedAsync(
                mode,
                localItems.Select(i => (i.EpisodeId, i.FileVersion)),
                configHash,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        foreach (var item in localItems)
        {
            item.AnalysisConfigHash = configHash;
        }

        // The cleanup journals the removed rows' projections, so they reach the
        // mirror even if the analyzers below detect nothing new.
        var subtitleOnlyItems = localItems
            .Where(item => item.GetAnalyzed(mode) == EpisodeState.NotAnalyzed && item.NeedsSubtitleOnlyReanalysis(mode))
            .ToArray();
        var conventionalItems = localItems
            .Where(item => !item.NeedsSubtitleOnlyReanalysis(mode))
            .ToArray();
        var creditsPass = mode == AnalysisMode.Credits
            ? new CreditsPass(_loggerFactory, _ffmpegService, _cacheService, _database, Config, AnalysisEnvironment ?? new PluginAnalysisEnvironment())
            : null;

        foreach (var eligibleItems in conventionalItems
            .Where(e => e.GetAnalyzed(mode) != EpisodeState.UserProvided && !(handledChapters?.Contains(e.EpisodeId) ?? false))
            .GroupBy(item => ShouldPreserveSubtitleRows(item, mode)))
        {
            await _database.CleanStaleAutomaticSegmentsAsync(
                eligibleItems.Select(e => e.EpisodeId),
                mode,
                configHash,
                eligibleItems.Key,
                cancellationToken).ConfigureAwait(false);
        }

        if (conventionalItems.Any(item => item.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
        {
            LogAnalyzingFiles(_logger, mode, conventionalItems.Length, first.SeriesName, first.SeasonNumber);
        }

        if (conventionalItems.Any(item => item.GetAnalyzed(mode) == EpisodeState.NotAnalyzed) && mode == AnalysisMode.Credits)
        {
            await SetCreditsWindowsAsync(conventionalItems, isMovie, cancellationToken).ConfigureAwait(false);

            // Credits settle chapter matches first by default; enhancement combines them
            // with other candidates. A single item cannot use chromaprint comparison.
            await creditsPass!.RunAsync(conventionalItems, action, ffmpegValid, cancellationToken, handledChapters).ConfigureAwait(false);
        }
        else if (conventionalItems.Any(item => item.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
        {
            await RunAnalyzerChainAsync(
                conventionalItems,
                mode,
                action,
                ffmpegValid,
                isMovie,
                cancellationToken,
                mode == AnalysisMode.Recap ? subtitleOnlyItems : null).ConfigureAwait(false);
        }

        if (subtitleOnlyItems.Length > 0)
        {
            await RunSubtitleOnlyAnalysisAsync(subtitleOnlyItems, mode, action, cancellationToken).ConfigureAwait(false);
        }

        // Credits-derived previews are generated right after a credits result lands, and again
        // in the Preview mode for episodes no preview analyzer settled, since a season whose
        // credits are all user-provided never enters the credits pass but its derived previews
        // still go stale when the preview settings change.
        var previewFromCreditsEnd = ShouldDerivePreview(first, Config);
        if (previewFromCreditsEnd)
        {
            if (mode == AnalysisMode.Credits)
            {
                var derivedItems = localItems.Where(item => IsLocallyEligible(localNode, item, AnalysisMode.Preview)
                    && !IsRemotelyOwned(remoteOwnedByMode, AnalysisMode.Preview, item.EpisodeId)).ToArray();
                if (derivedItems.Length > 0)
                {
                    var subtitlePreviewWillRun = (_remoteAnalysisDispatcher is not null || Config.ScanPreview)
                        && derivedItems.Any(item => IsSubtitleDetectionEnabled(item, AnalysisMode.Preview, Config));
                    await AnimePreviewDeriver.DeriveAsync(
                        _database,
                        derivedItems,
                        Config.MinimumPreviewDuration,
                        cancellationToken,
                        settlePreviewState: !subtitlePreviewWillRun).ConfigureAwait(false);
                }
            }
            else if (mode == AnalysisMode.Preview)
            {
                List<QueuedEpisode> unsettled = [.. localItems.Where(item => item.GetAnalyzed(AnalysisMode.Preview) is EpisodeState.NotAnalyzed or EpisodeState.NoSegments or EpisodeState.AnalysisFailed)];
                await AnimePreviewDeriver.DeriveAsync(_database, unsettled, Config.MinimumPreviewDuration, cancellationToken).ConfigureAwait(false);
            }
        }

        // Record completed items under this hash, found segments or not. Failed items are omitted so
        // a transient FFmpeg or analyzer failure remains eligible on the next scan.
        await _database.MarkItemsAnalyzedAsync(
            mode,
            localItems.Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed).Select(item => (item.EpisodeId, item.FileVersion)),
            configHash,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RunSubtitleOnlyAnalysisAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        AnalyzerAction action,
        CancellationToken cancellationToken)
    {
        var subtitleCandidates = new List<QueuedEpisode>(items.Count);
        foreach (var episode in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
            var hasChapterResult = segments.Any(segment => segment.Type == mode
                && segment.Source == SegmentSource.Chapter
                && segment.State == SegmentState.Active);
            var chromaprintHasPriority = mode == AnalysisMode.Recap
                && (action == AnalyzerAction.Chromaprint
                    || (action == AnalyzerAction.Default && Config.PreferChromaprint));
            var hasPreferredChromaprintResult = chromaprintHasPriority && segments.Any(segment => segment.Type == mode
                && segment.Source == SegmentSource.Chromaprint
                && segment.State == SegmentState.Active);
            var enabled = IsSubtitleDetectionEnabled(episode, mode, Config);
            var pattern = mode == AnalysisMode.Recap ? Config.SubtitleRecapPattern : Config.SubtitlePreviewPattern;

            if (hasChapterResult || hasPreferredChromaprintResult || !enabled || string.IsNullOrWhiteSpace(pattern))
            {
                if (segments.Any(segment => segment.Type == mode
                    && segment.Source == SegmentSource.Subtitle
                    && segment.State == SegmentState.Active))
                {
                    await _database.ClearSubtitleSegmentsAsync(episode.EpisodeId, mode, cancellationToken).ConfigureAwait(false);
                }

                episode.SetAnalyzed(mode, segments.Any(segment => segment.Type == mode
                    && segment.Source != SegmentSource.Subtitle
                    && segment.State == SegmentState.Active)
                    ? EpisodeState.Analyzed
                    : EpisodeState.NoSegments);
                continue;
            }

            subtitleCandidates.Add(episode);
        }

        if (subtitleCandidates.Count == 0)
        {
            return;
        }

        var analyzer = new SubtitleAnalyzer(
            _loggerFactory.CreateLogger<SubtitleAnalyzer>(),
            _ffmpegService,
            _database,
            Config,
            environment: AnalysisEnvironment);
        await analyzer.AnalyzeMediaFiles(subtitleCandidates, mode, cancellationToken).ConfigureAwait(false);

        foreach (var episode in subtitleCandidates)
        {
            if (episode.HasUnresolvedSubtitleDetection(mode))
            {
                episode.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
                continue;
            }

            if (episode.GetAnalyzed(mode) == EpisodeState.NotAnalyzed)
            {
                var hasActiveResult = (await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false))
                    .Any(segment => segment.Type == mode && segment.State == SegmentState.Active);
                episode.SetAnalyzed(mode, hasActiveResult ? EpisodeState.Analyzed : EpisodeState.NoSegments);
            }
        }
    }

    private static bool ShouldDerivePreview(QueuedEpisode episode, PluginConfiguration config)
        => episode.PreviewFromCreditsEndOverride
            ?? (episode.Category == QueuedMediaCategory.AnimeEpisode && config.AnimePreviewFromCreditsEnd);

    private bool ShouldPreserveSubtitleRows(QueuedEpisode episode, AnalysisMode mode)
        => mode switch
        {
            AnalysisMode.Recap => IsSubtitleDetectionEnabled(episode, mode, Config) && !string.IsNullOrWhiteSpace(Config.SubtitleRecapPattern),
            AnalysisMode.Preview => IsSubtitleDetectionEnabled(episode, mode, Config) && !string.IsNullOrWhiteSpace(Config.SubtitlePreviewPattern),
            _ => false,
        };

    private static bool IsSubtitleDetectionEnabled(QueuedEpisode episode, AnalysisMode mode, PluginConfiguration config)
        => mode == AnalysisMode.Recap
            ? episode.SubtitleRecapDetectionOverride ?? config.EnableSubtitleRecapDetection
            : mode == AnalysisMode.Preview
                && (episode.SubtitlePreviewDetectionOverride ?? config.EnableSubtitlePreviewDetection);

    private AnalysisMode[] GetSubtitleModesToPreserve(
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)> seasonStates,
        IReadOnlyList<QueuedEpisode> episodes)
        => [.. modes.Where(mode => episodes.Any(episode => ShouldPreserveSubtitleRows(episode, mode))
            && (!seasonStates.TryGetValue(mode, out var state) || state.Action != AnalyzerAction.None))];

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
        CancellationToken cancellationToken,
        IReadOnlyList<QueuedEpisode>? chromaprintReferences = null)
    {
        var modeEpisodes = items;

        // Subtitle matching is an opt-in, mode-specific fallback. It runs after the chapter
        // analyzer so chapter matches take precedence, and before the remaining default chain.
        // Chromaprint needs a season to compare (no movies) and a compatible ffmpeg.
        var subtitleEpisodes = modeEpisodes.Where(item => ShouldPreserveSubtitleRows(item, mode)).ToArray();
        var subtitle = subtitleEpisodes.Length > 0
            ? new SubtitleAnalyzer(_loggerFactory.CreateLogger<SubtitleAnalyzer>(), _ffmpegService, _database, Config, AnalysisEnvironment)
            : null;
        var chapter = new ChapterAnalyzer(
            _loggerFactory.CreateLogger<ChapterAnalyzer>(),
            _ffmpegService,
            _database,
            Config,
            enableRecapBlackFrameFallback: false,
            environment: AnalysisEnvironment);
        var recapBlackFrameFallback = mode == AnalysisMode.Recap && Config.DetectRecapUsingBlackFrames
            ? new ChapterAnalyzer(
                _loggerFactory.CreateLogger<ChapterAnalyzer>(),
                _ffmpegService,
                _database,
                Config,
                enableChapterDetection: false,
                environment: AnalysisEnvironment)
            : null;
        ChromaprintAnalyzer? chromaprint = ffmpegValid && !isMovie && mode is AnalysisMode.Introduction or AnalysisMode.Recap
            ? new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, Config, AnalysisEnvironment)
            : null;

        List<IMediaFileAnalyzer?> conventional = [chapter, chromaprint];

        // A per-season action, or the PreferChromaprint setting, moves one analyzer to the front;
        // the rest keep their relative order. An action naming an analyzer that is not in the
        // chain (Chromaprint without ffmpeg) changes nothing.
        IMediaFileAnalyzer? preferred = action switch
        {
            AnalyzerAction.Chapter => chapter,
            AnalyzerAction.Chromaprint => chromaprint,
            _ => Config.PreferChromaprint && ffmpegValid ? chromaprint : null,
        };
        List<IMediaFileAnalyzer> conventionalAnalyzers = [.. conventional.OfType<IMediaFileAnalyzer>()];
        if (preferred is not null && conventionalAnalyzers.Remove(preferred))
        {
            conventionalAnalyzers.Insert(0, preferred);
        }

        List<IMediaFileAnalyzer> analyzers = [.. conventionalAnalyzers];
        if (subtitle is not null)
        {
            analyzers.Insert(analyzers.IndexOf(chapter) + 1, subtitle);
        }

        if (recapBlackFrameFallback is not null)
        {
            IMediaFileAnalyzer precedingAnalyzer = subtitle is null ? chapter : subtitle;
            analyzers.Insert(analyzers.IndexOf(precedingAnalyzer) + 1, recapBlackFrameFallback);
        }

        foreach (var analyzer in analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items = analyzer == chromaprint && chromaprintReferences is { Count: > 0 }
                ? await chromaprint!.AnalyzeMediaFiles(items, mode, chromaprintReferences, cancellationToken).ConfigureAwait(false)
                : await analyzer.AnalyzeMediaFiles(items, mode, cancellationToken).ConfigureAwait(false);
        }

        if (subtitle is not null)
        {
            var completedFallbacks = subtitleEpisodes
                .Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed
                    && !item.HasUnresolvedSubtitleDetection(mode)
                    && !item.HasRejectedSubtitleCandidate(mode))
                .ToArray();
            await _database.CleanStaleAutomaticSegmentsAsync(
                completedFallbacks.Select(item => item.EpisodeId),
                mode,
                modeEpisodes[0].AnalysisConfigHash,
                preserveStaleRows: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var item in modeEpisodes.Where(item => item.HasUnresolvedSubtitleDetection(mode)))
            {
                item.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
            }

            foreach (var item in modeEpisodes.Where(item => item.HasRejectedSubtitleCandidate(mode)))
            {
                if (item.GetAnalyzed(mode) == EpisodeState.NotAnalyzed)
                {
                    item.SetAnalyzed(mode, EpisodeState.Analyzed);
                }
            }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "None of the {Count} requested items resolved to a season to analyze")]
    private static partial void LogNothingInScope(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to analyze {Series} season {Season} ({Key}); skipping it this pass")]
    private static partial void LogSeasonFailed(ILogger logger, Exception exception, string series, int season, Guid key);

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
