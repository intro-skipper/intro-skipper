// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Repositories;
using IntroSkipper.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
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
/// <param name="serviceProvider">Service provider for DI.</param>
public partial class BaseItemAnalyzerTask(
    ILogger logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IServiceProvider serviceProvider)
{
    private readonly ILogger _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
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
            _serviceProvider);

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

            // Segments are automatically synced via the outbox processor
            // No need to manually trigger updates here
        }).ConfigureAwait(false);

        Plugin.Instance!.AnalyzeAgain = false;

        if (_config.RebuildMediaSegments)
        {
            LogRegeneratedMediaSegments(_logger);
            _config.RebuildMediaSegments = false;
            Plugin.Instance!.SaveConfiguration();
        }
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments.
    /// </summary>
    /// <param name="items">Media items to analyze.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of items successfully analyzed.</returns>
    private async Task<int> AnalyzeItemsAsync(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (items.All(e => e.GetAnalyzed(mode)))
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

        var totalItems = items.Count(e => !e.GetAnalyzed(mode));
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");

        AnalyzerAction action;
        using (var scope = _serviceProvider.CreateScope())
        {
            var seasonRepository = scope.ServiceProvider.GetRequiredService<ISeasonRepository>();
            action = await seasonRepository.GetAnalyzerActionAsync(first.SeasonId, mode, cancellationToken).ConfigureAwait(false);
        }

        var chromaprintOnly = _ffmpegValid && _config.PreferChromaprint && action is AnalyzerAction.Default or AnalyzerAction.Chromaprint;

        LogAnalyzingFiles(_logger, mode, items.Count, first.SeriesName, first.SeasonNumber);

        // Create analyzers list
        var analyzers = new List<IMediaFileAnalyzer>();

        IMediaFileAnalyzer blackFrameAnalyzer = _config.UseAlternativeBlackFrameAnalyzer
            ? new BlackFrameAltAnalyzer(_loggerFactory.CreateLogger<BlackFrameAltAnalyzer>())
            : new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>());

        // Add analyzers based on conditions
        if (!chromaprintOnly && action is AnalyzerAction.Chapter or AnalyzerAction.Default)
        {
            analyzers.Add(new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>()));
        }

        if (isAnime && mode is AnalysisMode.Introduction or AnalysisMode.Credits && action is AnalyzerAction.Default or AnalyzerAction.Chromaprint && _ffmpegValid)
        {
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
        }

        if (!chromaprintOnly && mode is AnalysisMode.Credits && action is AnalyzerAction.Default or AnalyzerAction.BlackFrame)
        {
            analyzers.Add(blackFrameAnalyzer);
        }

        if (!isAnime && !isMovie && mode is AnalysisMode.Introduction or AnalysisMode.Credits && action is AnalyzerAction.Default or AnalyzerAction.Chromaprint && _ffmpegValid)
        {
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
        }

        // Use each analyzer to find skippable ranges in all media files, removing successfully
        // analyzed items from the queue.
        using (var scope = _serviceProvider.CreateScope())
        {
            var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentService>();

            foreach (var analyzer in analyzers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items = await analyzer.AnalyzeMediaFiles(items, mode, segmentService, cancellationToken).ConfigureAwait(false);
            }
        }

        // Episode IDs are now tracked implicitly via the segments table

        return totalItems - items.Count(e => !e.GetAnalyzed(mode));
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Regenerated media segments.")]
    private static partial void LogRegeneratedMediaSegments(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}")]
    private static partial void LogAnalyzingFiles(ILogger logger, AnalysisMode mode, int count, string name, int season);
}
