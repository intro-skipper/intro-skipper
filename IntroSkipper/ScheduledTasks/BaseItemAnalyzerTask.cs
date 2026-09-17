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
    private Guid? _shortcutBatchCursor;

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
    /// <param name="shortcutsOnly">Whether to analyze only shortcut media.</param>
    /// <param name="shortcutBatchSize">Maximum number of shortcut media items to include in this pass.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AnalyzeItemsAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? itemIds = null,
        bool shortcutsOnly = false,
        int shortcutBatchSize = 0)
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
        var owners = scope is null ? _seasonResolver.EnumerateLibrary(out _) : _seasonResolver.OwnersOf(scope);
        if (owners.Count == 0 && scope is null)
        {
            LogNoLibrariesSelected(_logger);
            return;
        }

        var options = new ParallelOptions
        {
            // Shortcut analysis is deliberately serialized: remote .strm targets may be
            // backed by a rate-limited service, and parallel seasons would defeat batching.
            MaxDegreeOfParallelism = shortcutsOnly ? 1 : Math.Max(1, Config.MaxParallelism),
            CancellationToken = cancellationToken
        };

        // The seasons stream out of the owners one series at a time as the parallel slots
        // free up. A series' episodes are in memory only while its seasons run, and the
        // slots share the seasons of one large series.
        await Parallel.ForEachAsync(
            SeasonsInScope(owners, scope, progress, shortcutsOnly, shortcutBatchSize),
            options,
            (season, ct) => new ValueTask(AnalyzeSeasonAsync(season, modes, ffmpegValid, shortcutsOnly, ct))).ConfigureAwait(false);
        progress.Report(100);
    }

    // Resolves the owners in order, yielding the seasons in scope: every season, or those
    // keyed by or holding a requested item. Shortcut passes retain whole seasons as
    // comparison context, but rotate the selected shortcut ids so a settled or failed
    // item cannot monopolize every batch.
    private IEnumerable<ResolvedSeason> SeasonsInScope(
        IReadOnlyList<BaseItem> owners,
        HashSet<Guid>? scope,
        IProgress<double> progress,
        bool shortcutsOnly,
        int shortcutBatchSize)
    {
        var yielded = 0;
        List<ResolvedSeason> resolvedSeasons = [];
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
                    resolvedSeasons.Add(season);
                }
            }
        }

        if (!shortcutsOnly)
        {
            foreach (var season in resolvedSeasons)
            {
                yielded++;
                yield return season;
            }
        }
        else
        {
            var shortcuts = resolvedSeasons
                .SelectMany(season => season.Episodes.Where(episode => episode.IsShortcut))
                .ToArray();
            if (shortcuts.Length > 0)
            {
                var batchSize = Math.Clamp(shortcutBatchSize, 1, PluginConfiguration.MaximumShortcutAnalysisBatchSize);
                var cursorIndex = _shortcutBatchCursor is { } cursor
                    ? Array.FindIndex(shortcuts, episode => episode.EpisodeId == cursor)
                    : -1;
                var start = cursorIndex >= 0 ? cursorIndex + 1 : 0;
                if (start >= shortcuts.Length)
                {
                    start = 0;
                }

                var selected = shortcuts
                    .Skip(start)
                    .Concat(shortcuts.Take(start))
                    .Take(batchSize)
                    .Select(episode => episode.EpisodeId)
                    .ToHashSet();
                _shortcutBatchCursor = shortcuts
                    .Skip(start)
                    .Concat(shortcuts.Take(start))
                    .Take(batchSize)
                    .Last()
                    .EpisodeId;

                foreach (var season in resolvedSeasons)
                {
                    var selectedIds = season.Episodes
                        .Where(episode => selected.Contains(episode.EpisodeId))
                        .Select(episode => episode.EpisodeId)
                        .ToHashSet();
                    if (selectedIds.Count == 0)
                    {
                        continue;
                    }

                    yielded++;
                    yield return season with { AnalysisItemIds = selectedIds };
                }
            }
        }

        if (scope is not null && yielded == 0)
        {
            LogNothingInScope(_logger, scope.Count);
        }
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
    private async Task AnalyzeSeasonAsync(ResolvedSeason season, IReadOnlyList<AnalysisMode> modes, bool ffmpegValid, bool shortcutsOnly, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnalysisMode> settledResetModes = [];

        var overrides = await _database.GetAnalysisOverridesAsync(season.Key, cancellationToken).ConfigureAwait(false);
        foreach (var episode in season.Episodes)
        {
            episode.AnalysisPercentOverride = overrides.AnalysisPercent;
            episode.AnalysisLengthLimitOverride = overrides.AnalysisLengthLimit;
            episode.PreviewFromCreditsEndOverride = overrides.PreviewFromCreditsEnd;

            var config = Config;
            var duration = episode.Duration;
            var analysisPercent = (overrides.AnalysisPercent ?? config.AnalysisPercent) / 100.0;
            var analysisLengthLimit = overrides.AnalysisLengthLimit ?? config.AnalysisLengthLimit;
            episode.IntroFingerprintEnd = Math.Min(
                duration >= 5 * 60 ? duration * analysisPercent : duration,
                60 * analysisLengthLimit);
        }

        var episodes = await VerifyQueueAsync(season.Episodes, modes, ffmpegValid, shortcutsOnly, season.AnalysisItemIds, cancellationToken).ConfigureAwait(false);
        var analysisTargets = episodes.Where(episode => episode.IsAnalysisTarget).ToArray();
        if (analysisTargets.Length == 0)
        {
            return;
        }

        if (shortcutsOnly)
        {
            // Classification is deliberately completed before any remote probe. A settled
            // shortcut may still be selected by the rotating cursor, but it must not spend
            // a rate-limited request or consume analysis work.
            analysisTargets = analysisTargets
                .Where(episode => modes.Any(mode => episode.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
                .ToArray();
            if (analysisTargets.Length == 0)
            {
                return;
            }
        }

        // A replaced file makes the old automatic segments and fingerprints wrong for every
        // mode, not only the ones this run covers. The fingerprints go first: the records
        // are the only evidence the file changed, so if the reset does not commit the
        // mismatch persists and the next run retries both. The reset journals its
        // deletions' projections. Every mode of the episode then reopens in memory too,
        // or a mode whose record matched would keep its settled state after its segments
        // were deleted and be recorded again with nothing behind it.
        var changedFiles = analysisTargets.Where(e => e.FileChanged).Select(e => e.EpisodeId).ToArray();
        if (changedFiles.Length > 0)
        {
            await _cacheDatabase.DeleteForItemsAsync(changedFiles, cancellationToken).ConfigureAwait(false);
            await _database.ResetItemsForReanalysisAsync(changedFiles, AllModes, cancellationToken).ConfigureAwait(false);
            foreach (var episode in analysisTargets.Where(e => e.FileChanged))
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

        if (shortcutsOnly)
        {
            await PrepareShortcutDurationsAsync(
                analysisTargets,
                changedFiles.ToHashSet(),
                cancellationToken).ConfigureAwait(false);
            analysisTargets = analysisTargets.Where(episode => episode.IsAnalysisTarget).ToArray();
            if (analysisTargets.Length == 0)
            {
                return;
            }
        }

        var first = analysisTargets[0];
        var previewFromCreditsEnd = ShouldDerivePreview(first, Config);

        // Run settled-season reanalysis from scratch after no new episodes have been added
        // for the configured delay so segments first derived from a partial season are
        // recomputed against the full season.
        // Reuses the cached fingerprints, so this only re-runs the comparison, not the decode.
        var utcNow = DateTime.UtcNow;
        var episodeIds = analysisTargets.Select(e => e.EpisodeId).ToArray();

        if (!previewFromCreditsEnd)
        {
            await _database.ClearCreditsDerivedPreviewsAsync(episodeIds, cancellationToken).ConfigureAwait(false);
        }

        // One season-state read serves both the settle decision and every mode's
        // analyzer action below.
        var seasonStates = await _database.GetSettleReanalysisStatesAsync(first.SeasonId, cancellationToken).ConfigureAwait(false);
        if (SeasonReanalysisPlanner.IsSettledForReanalysis(episodes, Config, utcNow))
        {
            settledResetModes = SeasonReanalysisPlanner.GetSettleReanalysisModes(seasonStates, episodeIds, modes, ffmpegValid);
            if (settledResetModes.Count > 0)
            {
                var resetModes = SeasonReanalysisPlanner.ExpandSettledResetModesForDerivedSegments(settledResetModes, previewFromCreditsEnd);
                LogReanalyzingSettledSeason(_logger, first.SeasonNumber, first.SeriesName, analysisTargets.Length);

                // The reset journals its deletions' projections, so they propagate
                // to Jellyfin even if the recompute finds nothing.
                await _database.ResetItemsForReanalysisAsync(episodeIds, resetModes, cancellationToken).ConfigureAwait(false);

                foreach (var episode in analysisTargets)
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
                    cancellationToken,
                    analysisTargets.Select(episode => episode.EpisodeId).ToHashSet()).ConfigureAwait(false);

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
    /// <param name="shortcutsOnly">Whether to verify only shortcut media.</param>
    /// <param name="analysisItemIds">Optional shortcut ids selected as analysis targets while the rest of the season remains comparison context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes that exist and are not excluded, classified per mode.</returns>
    internal async Task<IReadOnlyList<QueuedEpisode>> VerifyQueueAsync(
        IReadOnlyList<QueuedEpisode> candidates,
        IReadOnlyCollection<AnalysisMode> modes,
        bool ffmpegValid,
        bool shortcutsOnly = false,
        IReadOnlySet<Guid>? analysisItemIds = null,
        CancellationToken cancellationToken = default)
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
        if (candidates[0].AnalysisPercentOverride is null
            && candidates[0].AnalysisLengthLimitOverride is null
            && candidates[0].PreviewFromCreditsEndOverride is null
            && await LegacyAnalysisCompatibility.UpgradeAsync(_database, snapshot, config, cancellationToken).ConfigureAwait(false))
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
            previewFromCreditsEnd);

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

                // Refresh shortcut metadata because the item may have been re-resolved while
                // waiting in the queue. Keep Path as Jellyfin's library path; FFmpegService
                // selects AnalysisPath when it runs the actual media scan.
                candidate.IsShortcut = item.IsShortcut;
                candidate.ShortcutPath = item.ShortcutPath ?? string.Empty;
                candidate.IsAnalysisTarget = !shortcutsOnly
                    || analysisItemIds is null
                    || analysisItemIds.Contains(candidate.EpisodeId);

                if (!shortcutsOnly && candidate.IsShortcut)
                {
                    continue;
                }

                if (candidate.IsShortcut && !config.ProcessShortcutVideos)
                {
                    LogSkippingShortcutVideo(_logger, candidate.Name, candidate.EpisodeId);
                    continue;
                }

                if (candidate.IsShortcut && string.IsNullOrEmpty(candidate.ShortcutPath))
                {
                    LogSkippingShortcutWithoutPath(_logger, candidate.Name, candidate.EpisodeId);
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

                // Shortcut-only passes retain the rest of the season as comparison context.
                // Hydrate that context from the duration cache without probing it; selected
                // targets are hydrated and, when necessary, probed below.
                if (shortcutsOnly && !candidate.IsAnalysisTarget && candidate.IsShortcut)
                {
                    var cachedDuration = _cacheService.TryReadShortcutDuration(candidate, out var duration)
                        ? duration
                        : CachedShortcutDuration(snapshot, candidate);
                    if (cachedDuration is > 0)
                    {
                        candidate.Duration = cachedDuration.Value;
                        RecalculateFingerprintWindows(candidate, config);
                    }
                }

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

    private static void RecalculateFingerprintWindows(QueuedEpisode episode, PluginConfiguration config)
    {
        var analysisPercent = (episode.AnalysisPercentOverride ?? config.AnalysisPercent) / 100.0;
        var analysisLengthLimit = episode.AnalysisLengthLimitOverride ?? config.AnalysisLengthLimit;
        var fingerprintDuration = Math.Min(
            episode.Duration >= 5 * 60 ? episode.Duration * analysisPercent : episode.Duration,
            60 * analysisLengthLimit);

        episode.IntroFingerprintEnd = fingerprintDuration;
    }

    private async Task PrepareShortcutDurationsAsync(
        IReadOnlyList<QueuedEpisode> targets,
        IReadOnlySet<Guid> forceProbeIds,
        CancellationToken cancellationToken)
    {
        var snapshot = await _database.GetSeasonQueueSnapshotAsync(
            targets[0].SeasonId,
            [.. targets.Select(target => target.EpisodeId)],
            cancellationToken).ConfigureAwait(false);

        foreach (var candidate in targets.Where(target => target.IsShortcut))
        {
            double? duration = null;
            if (!forceProbeIds.Contains(candidate.EpisodeId)
                && _cacheService.TryReadShortcutDuration(candidate, out var cachedDuration))
            {
                duration = cachedDuration;
            }

            duration ??= CachedShortcutDuration(snapshot, candidate);
            if (duration is null
                && !forceProbeIds.Contains(candidate.EpisodeId)
                && !HasPreviousShortcutIdentity(snapshot, candidate)
                && candidate.Duration > 0)
            {
                duration = candidate.Duration;
            }

            duration ??= await _ffmpegService.ProbeDurationAsync(candidate.ShortcutPath, cancellationToken).ConfigureAwait(false);
            if (duration is not > 0)
            {
                LogSkippingShortcutWithoutDuration(_logger, candidate.Name, candidate.EpisodeId);
                candidate.IsAnalysisTarget = false;
                candidate.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.AnalysisFailed);
                candidate.SetAnalyzed(AnalysisMode.Recap, EpisodeState.AnalysisFailed);
                continue;
            }

            candidate.Duration = duration.Value;
            RecalculateFingerprintWindows(candidate, Config);
            _cacheService.WriteShortcutDuration(candidate, duration.Value);
        }
    }

    private static double? CachedShortcutDuration(SeasonQueueSnapshot snapshot, QueuedEpisode candidate)
    {
        foreach (var entry in snapshot.AnalysisRecords)
        {
            if (entry.Key.ItemId == candidate.EpisodeId
                && string.Equals(entry.Value.ShortcutPath, candidate.ShortcutPath, StringComparison.Ordinal)
                && entry.Value.FileVersion == candidate.FileVersion
                && entry.Value.Duration is > 0)
            {
                return entry.Value.Duration;
            }
        }

        return null;
    }

    private static bool HasPreviousShortcutIdentity(SeasonQueueSnapshot snapshot, QueuedEpisode candidate)
        => snapshot.AnalysisRecords.Any(entry =>
            entry.Key.ItemId == candidate.EpisodeId
            && (!string.Equals(entry.Value.ShortcutPath, candidate.ShortcutPath, StringComparison.Ordinal)
                || entry.Value.FileVersion != candidate.FileVersion));

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
    /// <param name="analysisItemIds">Optional item ids to analyze while other items remain comparison context.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal async Task AnalyzeItemsAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? analysisItemIds = null)
    {
        var analysisTargets = analysisItemIds is null
            ? items.Where(item => item.IsAnalysisTarget).ToArray()
            : items.Where(item => analysisItemIds.Contains(item.EpisodeId)).ToArray();

        // NoSegments is a negative-cache result for the current configuration; only an episode
        // reset to NotAnalyzed (new episode, configuration or Chromaprint-availability change,
        // settled-season reanalysis) reopens the season, and then NeedsAnalysis() gives the
        // settled episodes another chance too.
        if (!analysisTargets.Any(e => e.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
        {
            return;
        }

        var first = analysisTargets[0];
        var isMovie = first.Category == QueuedMediaCategory.Movie;

        if (AnalysisEligibility.IsSeasonZeroOptedOut(first, Config))
        {
            return;
        }

        var configHash = ConfigHasher.Analysis(
            Config,
            mode,
            action,
            ffmpegValid,
            first.AnalysisPercentOverride,
            first.AnalysisLengthLimitOverride,
            ShouldDerivePreview(first, Config));

        if (action == AnalyzerAction.None)
        {
            LogSkippingNoneAction(_logger, mode, first.SeriesName, first.SeasonNumber);
            // The disabled action is part of the hash. Persist it as settled so the same season is not
            // queued and skipped forever on every subsequent run.
            await _database.MarkItemsAnalyzedAsync(
                mode,
                analysisTargets.Select(AnalysisIdentity),
                configHash,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var item in analysisTargets)
        {
            item.AnalysisConfigHash = configHash;
        }

        // The cleanup journals the removed rows' projections, so they reach the
        // mirror even if the analyzers below detect nothing new.
        await _database.CleanStaleAutomaticSegmentsAsync(
            analysisTargets.Where(e => e.GetAnalyzed(mode) != EpisodeState.UserProvided).Select(e => e.EpisodeId),
            mode,
            configHash,
            cancellationToken).ConfigureAwait(false);

        LogAnalyzingFiles(_logger, mode, analysisTargets.Length, first.SeriesName, first.SeasonNumber);

        if (mode == AnalysisMode.Credits)
        {
            await SetCreditsWindowsAsync(analysisTargets, isMovie, cancellationToken).ConfigureAwait(false);

            // Credits settle chapter matches first by default; enhancement combines them
            // with other candidates. A single item cannot use chromaprint comparison.
            var pass = new CreditsPass(_loggerFactory, _ffmpegService, _cacheService, _database, Config);
            await pass.RunAsync(analysisTargets, action, ffmpegValid, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RunAnalyzerChainAsync(items, mode, action, ffmpegValid, isMovie, cancellationToken).ConfigureAwait(false);
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
                await AnimePreviewDeriver.DeriveAsync(_database, analysisTargets, Config.MinimumPreviewDuration, cancellationToken).ConfigureAwait(false);
            }
            else if (mode == AnalysisMode.Preview)
            {
                List<QueuedEpisode> unsettled = [.. analysisTargets.Where(item => item.NeedsAnalysis(AnalysisMode.Preview))];
                await AnimePreviewDeriver.DeriveAsync(_database, unsettled, Config.MinimumPreviewDuration, cancellationToken).ConfigureAwait(false);
            }
        }

        // Record completed items under this hash, found segments or not. Failed items are omitted so
        // a transient FFmpeg or analyzer failure remains eligible on the next scan.
        await _database.MarkItemsAnalyzedAsync(
            mode,
            analysisTargets
                .Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed && !item.IsComparisonPending(mode))
                .Select(AnalysisIdentity),
            configHash,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldDerivePreview(QueuedEpisode episode, PluginConfiguration config)
        => episode.PreviewFromCreditsEndOverride
            ?? (episode.Category == QueuedMediaCategory.AnimeEpisode && config.AnimePreviewFromCreditsEnd);

    private static (Guid ItemId, long? FileVersion, string? ShortcutPath, double? Duration) AnalysisIdentity(QueuedEpisode item)
        => (
            item.EpisodeId,
            item.FileVersion,
            item.IsShortcut ? item.ShortcutPath : null,
            item.IsShortcut && item.Duration > 0 ? item.Duration : null);

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
                var audioPath = item.IsShortcut && !string.IsNullOrEmpty(item.ShortcutPath)
                    ? item.ShortcutPath
                    : item.Path;
                var audioDuration = await _ffmpegService.ProbeAudioDurationAsync(audioPath, cancellationToken).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping {Name} ({Id}): file not found")]
    private static partial void LogSkippingFileNotFound(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping excluded item {Name}: matched {RuleLabel}")]
    private static partial void LogSkippingExcludedItem(ILogger logger, string name, string ruleLabel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping analysis of {Name} ({Id})")]
    private static partial void LogSkippingAnalysisException(ILogger logger, string name, Guid id, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping {Name} ({Id}): shortcut video processing is disabled")]
    private static partial void LogSkippingShortcutVideo(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping {Name} ({Id}): shortcut path is missing")]
    private static partial void LogSkippingShortcutWithoutPath(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping {Name} ({Id}): shortcut duration could not be probed")]
    private static partial void LogSkippingShortcutWithoutDuration(ILogger logger, string name, Guid id);

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
