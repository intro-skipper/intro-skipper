// SPDX-FileCopyrightText: 2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
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
/// <param name="libraryManager">Library manager.</param>
/// <param name="providerManager">Provider manager.</param>
/// <param name="fileSystem">File system.</param>
/// <param name="mediaSegmentUpdateManager">Media segment update manager.</param>
public partial class BaseItemAnalyzerTask(
    ILogger logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    MediaSegmentUpdateManager mediaSegmentUpdateManager)
{
    /// <summary>
    /// Tolerance (seconds) when comparing an existing anime Preview's start to a newly-computed
    /// credits.End. Chromaprint timestamps are quantised to ~0.124 s and a sub-second delta has
    /// no user-visible effect, so treat "close enough" as equal for idempotency.
    /// </summary>
    private const double AnimePreviewStartTolerance = 0.5;

    private readonly ILogger _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly bool _ffmpegValid = FFmpegWrapper.CheckFFmpegVersion();

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
        HashSet<AnalysisMode> modes = [
            .. _config.ScanIntroduction ? [AnalysisMode.Introduction] : Array.Empty<AnalysisMode>(),
            .. _config.ScanCredits ? [AnalysisMode.Credits] : Array.Empty<AnalysisMode>(),
            .. _config.ScanRecap ? [AnalysisMode.Recap] : Array.Empty<AnalysisMode>(),
            .. _config.ScanPreview ? [AnalysisMode.Preview] : Array.Empty<AnalysisMode>(),
            .. _config.ScanCommercial ? [AnalysisMode.Commercial] : Array.Empty<AnalysisMode>()
        ];

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager,
            _providerManager,
            _fileSystem);

        var queue = await queueManager.GetMediaItems(cancellationToken).ConfigureAwait(false);

        if (seasonsToAnalyze?.Count > 0)
        {
            queue = queue.Where(kvp => seasonsToAnalyze.Contains(kvp.Key))
                         .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        int totalQueued = queue.Sum(kvp => kvp.Value.Count) * modes.Count;
        if (totalQueued == 0)
        {
            LogNoLibrariesSelected(_logger);
            return;
        }

        if (!_ffmpegValid)
        {
            LogSkippingChromaprint(_logger);
        }

        int totalProcessed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _config.MaxParallelism),
            CancellationToken = cancellationToken
        };

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");

        await Parallel.ForEachAsync(queue, options, async (season, ct) =>
        {
            var updateMediaSegments = false;

            var episodes = await queueManager.VerifyQueueAsync(season.Value, modes, ct).ConfigureAwait(false);
            if (episodes.Count == 0)
            {
                return;
            }

            var first = episodes[0];
            if (first.IsExcluded)
            {
                Interlocked.Add(ref totalProcessed, episodes.Count * modes.Count);
                progress.Report((double)totalProcessed / totalQueued * 100);
                LogSkippingExcludedSeason(_logger, first.SeasonNumber, first.SeriesName);
                return;
            }

            try
            {
                foreach (var mode in modes)
                {
                    ct.ThrowIfCancellationRequested();
                    int analyzed = await AnalyzeItemsAsync(
                        plugin,
                        episodes,
                        mode,
                        ct).ConfigureAwait(false);
                    Interlocked.Add(ref totalProcessed, episodes.Count);

                    updateMediaSegments = analyzed > 0 || updateMediaSegments;
                    progress.Report((double)totalProcessed / totalQueued * 100);
                }
            }
            catch (OperationCanceledException)
            {
                LogAnalysisCanceled(_logger);
            }
            catch (FingerprintException ex)
            {
                LogFingerprintExceptionDuringAnalysis(_logger, ex);
            }
            catch (Exception ex)
            {
                LogUnexpectedAnalysisError(_logger, ex);
                throw;
            }

            if (updateMediaSegments && _config.UpdateMediaSegments)
            {
                await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync(episodes, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        plugin.AnalyzeAgain = false;
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments.
    /// </summary>
    /// <param name="plugin">Plugin instance.</param>
    /// <param name="items">Media items to analyze.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of items successfully analyzed.</returns>
    private async Task<int> AnalyzeItemsAsync(
        Plugin plugin,
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (!items.Any(e => e.GetAnalyzed(mode) == EpisodeState.NotAnalyzed))
        {
            return 0;
        }

        var first = items[0];
        var category = first.Category;
        var isMovie = category == QueuedMediaCategory.Movie;
        var isAnime = category == QueuedMediaCategory.AnimeEpisode;

        if (!isMovie && first.SeasonNumber == 0 && !_config.AnalyzeSeasonZero)
        {
            return 0;
        }

        var totalItems = items.Count(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed);
        var action = await plugin.GetAnalyzerActionAsync(first.SeasonId, mode, cancellationToken).ConfigureAwait(false);

        if (action == AnalyzerAction.None)
        {
            LogSkippingNoneAction(_logger, mode, first.SeriesName, first.SeasonNumber);
            return 0;
        }

        LogAnalyzingFiles(_logger, mode, items.Count, first.SeriesName, first.SeasonNumber);

        // Build the default analyzer chain for this mode and content type.
        // All applicable analyzers are always included — the order determines priority,
        // and each analyzer skips episodes already handled by earlier ones via NeedsAnalysis().
        var analyzers = new List<IMediaFileAnalyzer>
        {
            // ChapterAnalyzer: supports all modes and content types
            new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>())
        };

        if (mode is AnalysisMode.Credits)
        {
            if (isAnime)
            {
                // Anime credits: Chromaprint before BlackFrame (fingerprint matching preferred)
                if (_ffmpegValid)
                {
                    analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
                }

                analyzers.Add(CreateBlackFrameAnalyzer());
            }
            else
            {
                // Non-anime credits: BlackFrame before Chromaprint
                analyzers.Add(CreateBlackFrameAnalyzer());

                if (!isMovie && _ffmpegValid)
                {
                    analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
                }
            }
        }
        else if (mode is AnalysisMode.Introduction)
        {
            // Introduction: Chromaprint is the only non-chapter analyzer
            if (!isMovie && _ffmpegValid)
            {
                analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
            }
        }

        // Recap, Preview, Commercial: only ChapterAnalyzer (already added above)

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
                PromoteAnalyzer(analyzers, static a => a is BlackFrameAnalyzer or BlackFrameAltAnalyzer);
                break;
            default:
                if (_config.PreferChromaprint && _ffmpegValid)
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
        if (mode == AnalysisMode.Credits && isAnime && _config.AnimePreviewFromCreditsEnd)
        {
            await CreateAnimePreviewFromCreditsAsync(plugin, items, cancellationToken).ConfigureAwait(false);
        }

        // Set the episode IDs for the analyzed items
        await plugin.SetEpisodeIdsAsync(first.SeasonId, mode, items.Select(i => i.EpisodeId), cancellationToken).ConfigureAwait(false);

        return totalItems - items.Count(e => e.GetAnalyzed(mode) != EpisodeState.Analyzed);
    }

    /// <summary>
    /// Decide whether an anime Preview segment needs to be written for an episode, and build it.
    /// </summary>
    /// <remarks>
    /// Returns a new Segment when the Preview is missing, its Start no longer matches the current
    /// credits.End (e.g. because settings changed and Credits was re-analyzed), or its End no longer
    /// matches the episode duration (e.g. because the underlying media file was replaced).
    /// Returns <see langword="null"/> when there are no valid credits, the credits already cover the
    /// episode, or an existing Preview already matches both the current credits.End and the episode
    /// duration within or equal to <see cref="AnimePreviewStartTolerance"/>.
    /// </remarks>
    /// <param name="episodeId">Episode id.</param>
    /// <param name="episodeDuration">Episode duration in seconds.</param>
    /// <param name="existingTimestamps">Current segments keyed by mode for this episode.</param>
    /// <returns>Segment to write, or <see langword="null"/> when no write is needed.</returns>
    public static Segment? ComputeAnimePreviewFromCredits(
        Guid episodeId,
        double episodeDuration,
        IReadOnlyDictionary<AnalysisMode, Segment> existingTimestamps)
    {
        ArgumentNullException.ThrowIfNull(existingTimestamps);

        if (!existingTimestamps.TryGetValue(AnalysisMode.Credits, out var credits) || !credits.Valid)
        {
            return null;
        }

        if (credits.End >= episodeDuration)
        {
            return null;
        }

        if (existingTimestamps.TryGetValue(AnalysisMode.Preview, out var existing)
            && existing.Valid
            && Math.Abs(existing.Start - credits.End) <= AnimePreviewStartTolerance
            && Math.Abs(existing.End - episodeDuration) <= AnimePreviewStartTolerance)
        {
            return null;
        }

        return new Segment(episodeId, new TimeRange(credits.End, episodeDuration));
    }

    /// <summary>
    /// Creates the configured black frame analyzer variant.
    /// </summary>
    /// <returns>A <see cref="BlackFrameAnalyzer"/> or <see cref="BlackFrameAltAnalyzer"/> based on configuration.</returns>
    private IMediaFileAnalyzer CreateBlackFrameAnalyzer() => _config.UseAlternativeBlackFrameAnalyzer
        ? new BlackFrameAltAnalyzer(_loggerFactory.CreateLogger<BlackFrameAltAnalyzer>())
        : new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>());

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
    /// <param name="plugin">Plugin instance.</param>
    /// <param name="items">Media items to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task CreateAnimePreviewFromCreditsAsync(
        Plugin plugin,
        IReadOnlyList<QueuedEpisode> items,
        CancellationToken cancellationToken)
    {
        foreach (var episode in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Use GetSegmentsAsync (not GetTimestampsAsync) so we can see IsUserProvided.
            // UpdateTimestampAsync silently no-ops when a user-provided Preview exists, which would
            // otherwise leave us emitting a misleading "Created anime preview" log + Analyzed state.
            var dbSegments = await plugin.GetSegmentsAsync(episode.EpisodeId, cancellationToken).ConfigureAwait(false);

            if (dbSegments.Any(s => s.Type == AnalysisMode.Preview && s.IsUserProvided))
            {
                LogSkippedUserProvidedPreview(_logger, episode.Name);
                continue;
            }

            var timestamps = dbSegments
                .GroupBy(s => s.Type)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Start).First().ToSegment());

            var preview = ComputeAnimePreviewFromCredits(episode.EpisodeId, episode.Duration, timestamps);
            if (preview is null)
            {
                continue;
            }

            await plugin.UpdateTimestampAsync(preview, AnalysisMode.Preview, cancellationToken: cancellationToken).ConfigureAwait(false);
            episode.SetAnalyzed(AnalysisMode.Preview, EpisodeState.Analyzed);

            LogCreatedAnimePreview(_logger, episode.Name, preview.Start, preview.End);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Created anime preview for {Episode}: {Start:F2}s to {End:F2}s")]
    private static partial void LogCreatedAnimePreview(ILogger logger, string episode, double start, double end);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping anime preview for {Episode}: a user-provided Preview already exists.")]
    private static partial void LogSkippedUserProvidedPreview(ILogger logger, string episode);

    [LoggerMessage(Level = LogLevel.Information, Message = "No libraries selected for analysis. To enable, check library configuration > Media Segment Providers.")]
    private static partial void LogNoLibrariesSelected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping Chromaprint analysis! Chromaprint is not enabled in the current ffmpeg. If Jellyfin is running natively, install jellyfin-ffmpeg7. If Jellyfin is running in a container, upgrade to version 10.10.0 or newer.")]
    private static partial void LogSkippingChromaprint(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping excluded season {Season} of {Series}")]
    private static partial void LogSkippingExcludedSeason(ILogger logger, int season, string series);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis was canceled.")]
    private static partial void LogAnalysisCanceled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fingerprint exception during analysis.")]
    private static partial void LogFingerprintExceptionDuringAnalysis(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unexpected error occurred during analysis.")]
    private static partial void LogUnexpectedAnalysisError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}")]
    private static partial void LogAnalyzingFiles(ILogger logger, AnalysisMode mode, int count, string name, int season);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Skipping {Name} season {Season}: analyzer action is set to None")]
    private static partial void LogSkippingNoneAction(ILogger logger, AnalysisMode mode, string name, int season);
}
