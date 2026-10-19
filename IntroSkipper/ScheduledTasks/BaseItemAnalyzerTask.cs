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
using MediaBrowser.Model.Entities;
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
    DetectionCacheDatabase cacheDatabase,
    IntroSkipperDatabase database)
{
    private static readonly AnalysisMode[] AllModes = Enum.GetValues<AnalysisMode>();

    private readonly ILogger _logger = loggerFactory.CreateLogger<BaseItemAnalyzerTask>();
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly DetectionCacheService _cacheService = cacheService;
    private readonly DetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly IntroSkipperDatabase _database = database;

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
            (season, ct) => new ValueTask(TryAnalyzeSeasonAsync(season, modes, ffmpegValid, ct))).ConfigureAwait(false);
        progress.Report(100);
    }

    // A season that throws is logged and skipped so the pass goes on to the others: a
    // changed-items pass at the end of a library scan covers every item Jellyfin saved,
    // and one broken season must not cost the rest their analysis. Cancellation still
    // stops the pass.
    private async Task TryAnalyzeSeasonAsync(ResolvedSeason season, IReadOnlyList<AnalysisMode> modes, bool ffmpegValid, CancellationToken cancellationToken)
    {
        try
        {
            await AnalyzeSeasonAsync(season, modes, ffmpegValid, cancellationToken).ConfigureAwait(false);
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
    /// or a settled-season reanalysis invalidates, reads each episode's chapters, and runs
    /// every mode over the episodes whose chapters could be read.
    /// </summary>
    private async Task AnalyzeSeasonAsync(ResolvedSeason season, IReadOnlyList<AnalysisMode> modes, bool ffmpegValid, CancellationToken cancellationToken)
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
        // deletions' projections. Every mode of the episode then reopens in memory too,
        // or a mode whose record matched would keep its settled state after its segments
        // were deleted and be recorded again with nothing behind it.
        var changedFiles = episodes.Where(e => e.FileChanged).Select(e => e.EpisodeId).ToArray();
        if (changedFiles.Length > 0)
        {
            await _cacheDatabase.DeleteForItemsAsync(changedFiles, cancellationToken).ConfigureAwait(false);
            await _database.ResetItemsForReanalysisAsync(changedFiles, AllModes, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        // Reuses the cached fingerprints and scans, so this re-runs the comparison; only the credits
        // lead-in probe decodes its window again.
        var utcNow = DateTime.UtcNow;
        var episodeIds = episodes.Select(e => e.EpisodeId).ToArray();

        if (!previewFromCreditsEnd)
        {
            await _database.ClearCreditsDerivedPreviewsAsync(episodeIds, cancellationToken).ConfigureAwait(false);
        }

        // The season-state snapshot serves the settle decision and every mode's analyzer action.
        if (SeasonReanalysisPlanner.IsSettledForReanalysis(episodes, Config, utcNow))
        {
            settledResetModes = SeasonReanalysisPlanner.GetSettleReanalysisModes(seasonStates, episodeIds, modes, ffmpegValid);
            if (settledResetModes.Count > 0)
            {
                LogReanalyzingSettledSeason(_logger, first.SeasonNumber, first.SeriesName, episodes.Count);

                // The reset journals its deletions' projections, so they propagate
                // to Jellyfin even if the recompute finds nothing. A Credits reset never
                // reopens Preview. Keep automatic Credits rows as fallbacks until the
                // per-episode result decides whether to replace, clear, or retain them.
                // A failed scan must not blank the user's view. The final derive refreshes
                // previews from settled results and can derive a fallback from retained Credits
                // rows for unreadable or failed episodes.
                IReadOnlyCollection<AnalysisMode> preserveAutomaticModes = settledResetModes.Contains(AnalysisMode.Credits)
                    ? [AnalysisMode.Credits]
                    : [];
                await _database.ResetItemsForReanalysisAsync(
                    episodeIds,
                    settledResetModes,
                    GetSubtitleModesToPreserve(settledResetModes, seasonStates, episodes),
                    preserveAutomaticModes,
                    cancellationToken).ConfigureAwait(false);

                foreach (var episode in episodes)
                {
                    foreach (var resetMode in settledResetModes)
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

        // An episode whose chapters cannot be read sits out the modes, but the settled-season
        // record below keeps the full episode set, so one failed read does not reset the season
        // again on the next pass.
        var readable = await ReadChaptersAsync(episodes, modes, seasonStates, cancellationToken).ConfigureAwait(false);
        var completedSettledModes = new List<AnalysisMode>(settledResetModes.Count);

        try
        {
            foreach (var mode in modes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AnalyzeItemsAsync(
                    readable,
                    mode,
                    seasonStates.TryGetValue(mode, out var seasonState) ? seasonState.Action : AnalyzerAction.Default,
                    ffmpegValid,
                    cancellationToken).ConfigureAwait(false);

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

        // A Credits reset can change the source of derived previews even when every Credits
        // result is user-provided, so no episode reopens Credits for AnalyzeItemsAsync. Refresh
        // after all analyzer modes have had a chance to produce credits and Preview rows.
        if (completedSettledModes.Contains(AnalysisMode.Credits) && previewFromCreditsEnd)
        {
            var creditsAction = seasonStates.TryGetValue(AnalysisMode.Credits, out var creditsState)
                ? creditsState.Action
                : AnalyzerAction.Default;
            var creditsHash = ConfigHasher.Analysis(
                Config,
                AnalysisMode.Credits,
                creditsAction,
                ffmpegValid,
                first.AnalysisPercentOverride,
                first.AnalysisLengthLimitOverride,
                previewFromCreditsEnd,
                first.SubtitleRecapDetectionOverride,
                first.SubtitlePreviewDetectionOverride,
                chapterSource: first.ChaptersSource);
            List<QueuedEpisode> creditsPreviewEpisodes = [.. episodes
                .Where(episode => episode.GetAnalyzed(AnalysisMode.Credits) is EpisodeState.Analyzed or EpisodeState.NoSegments or EpisodeState.UserProvided)];
            foreach (var episode in creditsPreviewEpisodes)
            {
                episode.AnalysisConfigHash = creditsHash;
            }

            // A failed or unreadable episode can still derive from its retained Credits rows.
            // Use their hash so refreshing or creating that fallback Preview does not make it
            // appear newer than the Credits result that feeds it.
            foreach (var episode in episodes.Where(episode => episode.GetAnalyzed(AnalysisMode.Credits) is EpisodeState.NotAnalyzed or EpisodeState.AnalysisFailed))
            {
                var retainedCredits = await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false);
                var retainedCreditsHashes = retainedCredits
                    .Where(segment => segment.Type == AnalysisMode.Credits && segment.State == SegmentState.Active && segment.Source != SegmentSource.User)
                    .Select(segment => segment.ConfigHash)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (retainedCreditsHashes.Length != 1)
                {
                    continue;
                }

                episode.AnalysisConfigHash = retainedCreditsHashes[0];
                creditsPreviewEpisodes.Add(episode);
            }

            await AnimePreviewDeriver.DeriveAsync(
                _database,
                creditsPreviewEpisodes,
                Config.MinimumPreviewDuration,
                cancellationToken).ConfigureAwait(false);
        }

        // No mirror push here: every write above journaled its item's projection
        // with the change, and the projection worker converges Jellyfin durably.
        if (completedSettledModes.Count > 0)
        {
            await _database.RecordSettleReanalysisAsync(first.SeasonId, completedSettledModes, episodeIds, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the chapter source checks the verifier flagged, then reads each episode's
    /// chapters from the chapter source into <see cref="QueuedEpisode.Chapters"/>, before any
    /// mode runs.
    /// </summary>
    /// <remarks>
    /// Step 5 of the season's pass in <c>docs/analysis.md</c> describes the read and the chapter
    /// source check in full. The invariants: a check (<see cref="QueuedEpisode.ChapterSourceChecks"/>)
    /// whose two sources hold the same chapters is re-stamped through
    /// <see cref="IntroSkipperDatabase.UpgradeAnalysisHashAsync"/> and runs no analyzer, unless
    /// its hash names another source than the one this pass read, in which case the record
    /// already names the source read; a check whose sources differ, or whose other source cannot
    /// be read, reopens the mode. Beyond the checks, nothing is read unless <see cref="IsModeOpen"/>
    /// holds for a mode whose action is not None, and an opted-out Season 0 reads nothing. An
    /// episode whose embedded chapters ffprobe cannot read is logged, left out of the result when
    /// a mode runs, and keeps its rows; without a record it is retried on the next pass. A failed
    /// read of Jellyfin's chapter table throws and fails the season. Each episode records the
    /// source it was read from in <see cref="QueuedEpisode.ChaptersSource"/>, which the modes
    /// hash instead of the live setting.
    /// </remarks>
    /// <param name="episodes">The season's verified episodes.</param>
    /// <param name="modes">Analysis modes of the run.</param>
    /// <param name="seasonStates">The season's analyzer action per mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes whose chapters were read, or every episode when no mode will run an analyzer.</returns>
    private async Task<IReadOnlyList<QueuedEpisode>> ReadChaptersAsync(
        IReadOnlyList<QueuedEpisode> episodes,
        IReadOnlyList<AnalysisMode> modes,
        IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)> seasonStates,
        CancellationToken cancellationToken)
    {
        var config = Config;
        var first = episodes[0];
        if (AnalysisEligibility.IsSeasonZeroOptedOut(first, config))
        {
            return episodes;
        }

        var source = config.ChapterSource;
        var unreadable = new HashSet<Guid>();

        // Reads the episode's chapters from a source; null when ffprobe cannot read the file.
        async Task<IReadOnlyList<ChapterInfo>?> ReadSourceAsync(QueuedEpisode episode, ChapterSource from)
            => from == ChapterSource.JellyfinTable
                ? Plugin.Instance!.GetChapters(episode.EpisodeId)
                : await _ffmpegService.GetEmbeddedChaptersAsync(episode, cancellationToken).ConfigureAwait(false);

        // Reads the source in effect into the episode; false when ffprobe cannot read the file.
        async Task<bool> ReadAsync(QueuedEpisode episode)
        {
            if (await ReadSourceAsync(episode, source).ConfigureAwait(false) is { } chapters)
            {
                episode.Chapters = chapters;
                episode.ChaptersSource = source;
                return true;
            }

            LogEmbeddedChaptersUnreadable(_logger, episode.Name, episode.EpisodeId);
            unreadable.Add(episode.EpisodeId);
            return false;
        }

        // The checks go first: a difference reopens modes, which gives the season work.
        var checks = episodes.Where(episode => episode.ChapterSourceChecks.Count > 0).ToArray();
        var restamps = new Dictionary<(AnalysisMode Mode, ChapterSourceCheck Check), List<Guid>>();
        var kept = 0;
        var reopened = 0;
        foreach (var episode in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ReadAsync(episode).ConfigureAwait(false))
            {
                continue;
            }

            var other = await ReadSourceAsync(episode, source == ChapterSource.Embedded ? ChapterSource.JellyfinTable : ChapterSource.Embedded).ConfigureAwait(false);
            var same = other is not null && SameChapters(episode.Chapters, other);
            var restamped = false;
            var reopens = false;
            foreach (var (mode, check) in episode.ChapterSourceChecks)
            {
                // An earlier step of the pass may have reset the mode already.
                if (episode.GetAnalyzed(mode) is not (EpisodeState.Analyzed or EpisodeState.NoSegments))
                {
                    continue;
                }

                if (!same)
                {
                    episode.SetAnalyzed(mode, EpisodeState.NotAnalyzed);
                    reopens = true;
                }
                else if (check.Source == source)
                {
                    if (!restamps.TryGetValue((mode, check), out var ids))
                    {
                        restamps[(mode, check)] = ids = [];
                    }

                    ids.Add(episode.EpisodeId);
                    restamped = true;
                }
            }

            episode.ClearChapterSourceChecks();
            if (reopens)
            {
                reopened++;
            }
            else if (restamped)
            {
                kept++;
            }
        }

        foreach (var ((mode, check), ids) in restamps)
        {
            await _database.UpgradeAnalysisHashAsync(mode, ids, check.StoredHash, check.CurrentHash, cancellationToken).ConfigureAwait(false);
        }

        if (checks.Length > 0)
        {
            LogChapterSourceChecked(_logger, first.SeriesName, first.SeasonNumber, kept, reopened);
        }

        var analyzerRuns = modes.Any(mode =>
            (!seasonStates.TryGetValue(mode, out var state) || state.Action != AnalyzerAction.None)
            && IsModeOpen(episodes, mode, config));
        if (!analyzerRuns)
        {
            return episodes;
        }

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (episode.ChaptersSource is null && !unreadable.Contains(episode.EpisodeId))
            {
                await ReadAsync(episode).ConfigureAwait(false);
            }
        }

        return unreadable.Count == 0 ? episodes : [.. episodes.Where(episode => !unreadable.Contains(episode.EpisodeId))];
    }

    // Start and name are all the analyzers read from a chapter.
    private static bool SameChapters(IReadOnlyList<ChapterInfo> first, IReadOnlyList<ChapterInfo> second)
        => first.Count == second.Count
            && first.Zip(second).All(pair => pair.First.StartPositionTicks == pair.Second.StartPositionTicks
                && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal));

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
        var previewFromCreditsEnd = ShouldDerivePreview(candidates[0], config);
        if (await LegacyAnalysisCompatibility.UpgradeAsync(
                _database,
                snapshot,
                config,
                GetLegacyHashAdoptionExclusions(candidates[0], config),
                previewFromCreditsEnd,
                cancellationToken).ConfigureAwait(false))
        {
            snapshot = await _database.GetSeasonQueueSnapshotAsync(candidates[0].SeasonId, [.. candidates.Select(c => c.EpisodeId)], cancellationToken).ConfigureAwait(false);
        }

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
        var sidecarFolders = new SubtitleSidecarFiles.FolderListings();

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
                if (config.ActiveSubtitlePattern(candidate, AnalysisMode.Recap) is not null
                    || config.ActiveSubtitlePattern(candidate, AnalysisMode.Preview) is not null)
                {
                    candidate.InternalMetadataPath = item.GetInternalMetadataPath();
                    candidate.SubtitleFileVersion = SubtitleSidecarFiles.FileVersion(candidate, SubtitleLanguageSelection.Parse(config.SubtitleLanguages), sidecarFolders);
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

    // 10.11 hashed the plugin-wide values, so a season override that differs from them means
    // the 10.11 result was made under other settings. Recap and Preview are never adopted.
    private static IReadOnlySet<AnalysisMode> GetLegacyHashAdoptionExclusions(QueuedEpisode episode, PluginConfiguration config)
    {
        var excludedModes = new HashSet<AnalysisMode>();
        if ((episode.AnalysisPercentOverride is { } analysisPercent && analysisPercent != config.AnalysisPercent)
            || (episode.AnalysisLengthLimitOverride is { } analysisLengthLimit && analysisLengthLimit != config.AnalysisLengthLimit))
        {
            excludedModes.Add(AnalysisMode.Introduction);
        }

        if (episode.PreviewFromCreditsEndOverride is { } previewFromCreditsEnd
            && previewFromCreditsEnd != config.AnimePreviewFromCreditsEnd)
        {
            excludedModes.Add(AnalysisMode.Credits);
        }

        return excludedModes;
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments. Every write into the
    /// segment store journals its item's projection, so the Jellyfin mirror converges
    /// from the journal; the pass itself never pushes.
    /// </summary>
    /// <remarks>
    /// The analyzers read each item's chapters only from <see cref="QueuedEpisode.Chapters"/>,
    /// which the season pass fills through <see cref="ReadChaptersAsync"/> before any mode
    /// runs. A caller that skips the season pass sets them itself.
    /// </remarks>
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
        if (!IsModeOpen(items, mode, Config))
        {
            return;
        }

        var first = items[0];
        var isMovie = first.Category == QueuedMediaCategory.Movie;

        var configHash = ConfigHasher.Analysis(
            Config,
            mode,
            action,
            ffmpegValid,
            first.AnalysisPercentOverride,
            first.AnalysisLengthLimitOverride,
            ShouldDerivePreview(first, Config),
            first.SubtitleRecapDetectionOverride,
            first.SubtitlePreviewDetectionOverride,
            chapterSource: first.ChaptersSource);

        if (action == AnalyzerAction.None)
        {
            LogSkippingNoneAction(_logger, mode, first.SeriesName, first.SeasonNumber);
            // The disabled action is part of the hash. Persist it as settled so the same season is not
            // queued and skipped forever on every subsequent run.
            await _database.MarkItemsAnalyzedAsync(
                mode,
                items.Select(i => (i.EpisodeId, Config.RecordedFileVersion(i, mode))),
                configHash,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var item in items)
        {
            item.AnalysisConfigHash = configHash;
        }

        // Episodes where only what subtitle detection reads changed rerun the subtitle analyzer
        // alone; the ones it hands back join the full chain below.
        var subtitleOnlyItems = items
            .Where(item => item.GetAnalyzed(mode) == EpisodeState.NotAnalyzed && item.NeedsSubtitleOnlyReanalysis(mode))
            .ToArray();
        if (subtitleOnlyItems.Length > 0)
        {
            await RunSubtitleOnlyAnalysisAsync(subtitleOnlyItems, mode, action, cancellationToken).ConfigureAwait(false);
        }

        var conventionalItems = items
            .Where(item => !item.NeedsSubtitleOnlyReanalysis(mode))
            .ToArray();

        // The cleanup journals the removed rows' projections, so they reach the mirror even
        // if the analyzers below detect nothing new. It covers only the episodes this pass
        // analyzes: an episode an earlier pass settled keeps its rows when a sibling reopens
        // the mode, such as the rows a subtitle-only pass kept under their old hash. With
        // subtitle detection active, the chain cleans up after itself instead.
        var cleanableItems = conventionalItems
            .Where(item => item.NeedsAnalysis(mode) && Config.ActiveSubtitlePattern(item, mode) is null);
        QueuedEpisode[] cleanableItemsSnapshot = [.. cleanableItems];
        // Credits must inspect standing rows when a keyframe scan fails, even if those rows
        // were written under an older hash. Clean only after a result settles; failed episodes
        // retain their prior credits for the next pass.
        if (mode != AnalysisMode.Credits)
        {
            await _database.CleanStaleAutomaticSegmentsAsync(
                cleanableItemsSnapshot.Select(item => item.EpisodeId),
                mode,
                configHash,
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
            var pass = new CreditsPass(_loggerFactory, _ffmpegService, _cacheService, _database, Config);
            await pass.RunAsync(conventionalItems, action, ffmpegValid, cancellationToken).ConfigureAwait(false);

            await _database.CleanStaleAutomaticSegmentsAsync(
                cleanableItemsSnapshot
                    .Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed)
                    .Select(item => item.EpisodeId),
                mode,
                configHash,
                cancellationToken).ConfigureAwait(false);
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
                mode == AnalysisMode.Recap ? [.. subtitleOnlyItems.Where(item => item.NeedsSubtitleOnlyReanalysis(mode))] : null).ConfigureAwait(false);
        }

        // Credits-derived previews are generated after settled credits results, and again in
        // Preview mode for episodes no preview analyzer settled, since a season whose credits
        // are all user-provided never enters the credits pass but its derived previews still
        // go stale when preview settings change. Failed Credits episodes retain their prior
        // rows as a pair until a later pass can settle a new result.
        var previewFromCreditsEnd = ShouldDerivePreview(first, Config);
        if (previewFromCreditsEnd)
        {
            if (mode == AnalysisMode.Credits)
            {
                // Subtitle detection in the Preview mode later in this pass can still supersede
                // the derived preview, so the derive leaves the Preview state to it.
                var subtitlePreviewWillRun = Config.ScanPreview && Config.ActiveSubtitlePattern(first, AnalysisMode.Preview) is not null;
                QueuedEpisode[] settledCreditsItems = [.. items
                    .Where(item => item.GetAnalyzed(AnalysisMode.Credits) is EpisodeState.Analyzed or EpisodeState.NoSegments or EpisodeState.UserProvided)];
                await AnimePreviewDeriver.DeriveAsync(
                    _database,
                    settledCreditsItems,
                    Config.MinimumPreviewDuration,
                    cancellationToken,
                    settlePreviewState: !subtitlePreviewWillRun).ConfigureAwait(false);
            }
            else if (mode == AnalysisMode.Preview)
            {
                List<QueuedEpisode> unsettled = [.. items.Where(item => item.GetAnalyzed(AnalysisMode.Preview) is EpisodeState.NotAnalyzed or EpisodeState.NoSegments or EpisodeState.AnalysisFailed)];
                await AnimePreviewDeriver.DeriveAsync(_database, unsettled, Config.MinimumPreviewDuration, cancellationToken).ConfigureAwait(false);
            }
        }

        // Record completed items under this hash, found segments or not. Failed items are omitted so
        // a transient FFmpeg or analyzer failure remains eligible on the next scan.
        await _database.MarkItemsAnalyzedAsync(
            mode,
            items.Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed)
                .Select(item => (item.EpisodeId, Config.RecordedFileVersion(item, mode))),
            configHash,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reruns subtitle detection alone for episodes whose conventional result still holds
    /// (<see cref="QueuedEpisode.NeedsSubtitleOnlyReanalysis"/>). A chapter result, or a
    /// Chromaprint result the season prefers, outranks any subtitle match, so the episode keeps
    /// it and loses its subtitle rows without a scan; so does an episode whose subtitle
    /// detection is no longer active. An episode whose standing result came from subtitles
    /// never ran the analyzers ranked below them, so its mark is cleared and the caller runs
    /// the full chain for it. The rest get the subtitle analyzer, settled like the chain
    /// settles it.
    /// </summary>
    private async Task RunSubtitleOnlyAnalysisAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        AnalyzerAction action,
        CancellationToken cancellationToken)
    {
        var chromaprintHasPriority = mode == AnalysisMode.Recap
            && (action == AnalyzerAction.Chromaprint
                || (action == AnalyzerAction.Default && Config.PreferChromaprint));
        var subtitleCandidates = new List<QueuedEpisode>(items.Count);
        foreach (var episode in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = (await _database.GetSegmentsAsync(episode.EpisodeId, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Where(segment => segment.Type == mode && segment.State == SegmentState.Active && segment.Source != SegmentSource.User)
                .ToList();
            var outranksSubtitles = rows.Any(row => row.Source == SegmentSource.Chapter
                || (chromaprintHasPriority && row.Source == SegmentSource.Chromaprint));
            if (!outranksSubtitles && rows.Any(row => row.Source == SegmentSource.Subtitle))
            {
                episode.ClearSubtitleOnlyReanalysis(mode);
                continue;
            }

            if (outranksSubtitles || Config.ActiveSubtitlePattern(episode, mode) is null)
            {
                if (rows.Any(row => row.Source == SegmentSource.Subtitle))
                {
                    await _database.ClearSubtitleSegmentsAsync(episode.EpisodeId, mode, cancellationToken).ConfigureAwait(false);
                }

                episode.SetAnalyzed(mode, rows.Any(row => row.Source != SegmentSource.Subtitle) ? EpisodeState.Analyzed : EpisodeState.NoSegments);
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
            Config);
        await analyzer.AnalyzeMediaFiles(subtitleCandidates, mode, cancellationToken).ConfigureAwait(false);

        foreach (var episode in subtitleCandidates)
        {
            if (episode.GetSubtitleOutcome(mode) == SubtitleOutcome.Unresolved)
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

    /// <summary>
    /// Determines whether a mode has work in a season: some episode is open in the mode and the
    /// season is not an opted-out Season 0.
    /// </summary>
    /// <remarks>
    /// <see cref="AnalyzeItemsAsync(IReadOnlyList{QueuedEpisode}, AnalysisMode, AnalyzerAction, bool, CancellationToken)"/>
    /// returns at once when this is false, and with a None action records the episodes without
    /// running an analyzer. <see cref="ReadChaptersAsync"/> reads chapters only when this holds
    /// for a mode whose action is not None. Both use this one check, so an analyzer never runs
    /// over episodes whose chapters were not read.
    /// </remarks>
    /// <param name="episodes">The season's episodes, possibly none.</param>
    /// <param name="mode">The analysis mode.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns><see langword="true"/> if the mode has work in the season; otherwise, <see langword="false"/>.</returns>
    private static bool IsModeOpen(IReadOnlyList<QueuedEpisode> episodes, AnalysisMode mode, PluginConfiguration config)
        => episodes.Any(episode => episode.GetAnalyzed(mode) == EpisodeState.NotAnalyzed)
            && !AnalysisEligibility.IsSeasonZeroOptedOut(episodes[0], config);

    private static bool ShouldDerivePreview(QueuedEpisode episode, PluginConfiguration config)
        => episode.PreviewFromCreditsEndOverride
            ?? (episode.Category == QueuedMediaCategory.AnimeEpisode && config.AnimePreviewFromCreditsEnd);

    private static AnalysisMode[] GetSubtitleModesToPreserve(
        IReadOnlyCollection<AnalysisMode> modes,
        IReadOnlyDictionary<AnalysisMode, (AnalyzerAction Action, IReadOnlySet<Guid> SettledReanalysisEpisodeIds)> seasonStates,
        IReadOnlyList<QueuedEpisode> episodes)
        => [.. modes.Where(mode => episodes.Any(episode => Config.ActiveSubtitlePattern(episode, mode) is not null)
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
        // Subtitle matching is an opt-in, mode-specific fallback. It runs after the chapter
        // analyzer so chapter matches take precedence, and before the remaining default chain.
        // Chromaprint needs a season to compare (no movies) and a compatible ffmpeg. The
        // cleanup after the chain covers only the episodes this pass analyzes, so an episode
        // an earlier pass settled keeps the rows a rejected match left standing.
        var subtitleEpisodes = items.Where(item => item.NeedsAnalysis(mode) && Config.ActiveSubtitlePattern(item, mode) is not null).ToArray();
        var configHash = items[0].AnalysisConfigHash;
        var subtitle = subtitleEpisodes.Length > 0
            ? new SubtitleAnalyzer(_loggerFactory.CreateLogger<SubtitleAnalyzer>(), _ffmpegService, _database, Config)
            : null;
        var chapter = new ChapterAnalyzer(
            _loggerFactory.CreateLogger<ChapterAnalyzer>(),
            _ffmpegService,
            _database,
            Config,
            enableRecapBlackFrameFallback: false);
        var recapBlackFrameFallback = mode == AnalysisMode.Recap && Config.DetectRecapUsingBlackFrames
            ? new ChapterAnalyzer(
                _loggerFactory.CreateLogger<ChapterAnalyzer>(),
                _ffmpegService,
                _database,
                Config,
                enableChapterDetection: false)
            : null;
        ChromaprintAnalyzer? chromaprint = ffmpegValid && !isMovie && mode is AnalysisMode.Introduction or AnalysisMode.Recap
            ? new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>(), _ffmpegService, _cacheService, _database, Config)
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

        if (subtitle is null)
        {
            return;
        }

        // Unresolved and rejected episodes keep their stale rows as the fallback. Unresolved
        // ones fail so a later pass retries them; rejected ones settle, since a retry would
        // end the same way.
        await _database.CleanStaleAutomaticSegmentsAsync(
            subtitleEpisodes
                .Where(item => item.GetAnalyzed(mode) != EpisodeState.AnalysisFailed && item.GetSubtitleOutcome(mode) == SubtitleOutcome.None)
                .Select(item => item.EpisodeId),
            mode,
            configHash,
            cancellationToken).ConfigureAwait(false);

        foreach (var item in subtitleEpisodes)
        {
            switch (item.GetSubtitleOutcome(mode))
            {
                case SubtitleOutcome.Unresolved:
                    item.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
                    break;
                case SubtitleOutcome.Rejected when item.GetAnalyzed(mode) == EpisodeState.NotAnalyzed:
                    item.SetAnalyzed(mode, EpisodeState.Analyzed);
                    break;
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the embedded chapters of {Name} ({Id}); it is skipped until the next pass")]
    private static partial void LogEmbeddedChaptersUnreadable(ILogger logger, string name, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chapter source check for {Series} season {Season}: {Kept} episodes hold the same chapters in both sources and keep their analysis, {Reopened} are analyzed again")]
    private static partial void LogChapterSourceChecked(ILogger logger, string series, int season, int kept, int reopened);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "An ffmpeg scan timed out during analysis; skipping this season. " + FFmpegProcessRunner.ScanTimeoutHint)]
    private static partial void LogFfmpegTimeoutDuringAnalysis(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}")]
    private static partial void LogAnalyzingFiles(ILogger logger, AnalysisMode mode, int count, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Skipping {Name} season {Season}: analyzer action is set to None")]
    private static partial void LogSkippingNoneAction(ILogger logger, AnalysisMode mode, string name, int season);
}
