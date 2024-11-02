// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Library;
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
/// <param name="mediaSegmentUpdateManager">MediaSegmentUpdateManager.</param>
public class BaseItemAnalyzerTask(
    ILogger logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    MediaSegmentUpdateManager mediaSegmentUpdateManager)
{
    private readonly ILogger _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;
    private readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Analyze all media items on the server.
    /// </summary>
    /// <param name="progress">Progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="seasonsToAnalyze">Season Ids to analyze.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task AnalyzeItems(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? seasonsToAnalyze = null)
    {
        var ffmpegValid = FFmpegWrapper.CheckFFmpegVersion();
        // Assert that ffmpeg with chromaprint is installed
        if (_config.WithChromaprint && !ffmpegValid)
        {
            throw new FingerprintException(
                "Analysis terminated! Chromaprint is not enabled in the current ffmpeg. If Jellyfin is running natively, install jellyfin-ffmpeg5. If Jellyfin is running in a container, upgrade to version 10.8.0 or newer.");
        }

        HashSet<AnalysisMode> modes = [
            .. _config.ScanIntroduction ? [AnalysisMode.Introduction] : Array.Empty<AnalysisMode>(),
            .. _config.ScanCredits ? [AnalysisMode.Credits] : Array.Empty<AnalysisMode>(),
            .. _config.ScanRecap ? [AnalysisMode.Recap] : Array.Empty<AnalysisMode>(),
            .. _config.ScanPreview ? [AnalysisMode.Preview] : Array.Empty<AnalysisMode>()
        ];

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager);

        var queue = queueManager.GetMediaItems();

        // Filter the queue based on seasonsToAnalyze
        if (seasonsToAnalyze is { Count: > 0 })
        {
            queue = queue.Where(kvp => seasonsToAnalyze.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        int totalQueued = queue.Sum(kvp => kvp.Value.Count) * modes.Count;
        if (totalQueued == 0)
        {
            throw new FingerprintException(
                "No libraries selected for analysis. Please visit the plugin settings to configure.");
        }

        var totalProcessed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _config.MaxParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(queue, options, async (season, ct) =>
        {
            var updateManagers = false;

            // Since the first run of the task can run for multiple hours, ensure that none
            // of the current media items were deleted from Jellyfin since the task was started.
            var (episodes, requiredModes) = queueManager.VerifyQueue(
                season.Value,
                _analysisModes);

            if (episodes.Count == 0)
            {
                return;
            }

            var first = episodes[0];
            if (modes.Count != requiredModes.Count)
            {
                Interlocked.Add(ref totalProcessed, episodes.Count * (modes.Count - requiredModes.Count));
                progress.Report(totalProcessed * 100 / totalQueued); // Partial analysis some modes have already been analyzed
            }

            try
            {
                foreach (AnalysisMode mode in requiredModes)
                {
                    var action = Plugin.Instance!.GetAnalyzerAction(season.Key, mode);
                    var analyzed = await AnalyzeItems(episodes, mode, action, ct).ConfigureAwait(false);
                    Interlocked.Add(ref totalProcessed, analyzed);

                    updateManagers = analyzed > 0 || updateManagers;

                    progress.Report(totalProcessed * 100 / totalQueued);
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogDebug(ex, "Analysis cancelled");
            }
            catch (FingerprintException ex)
            {
                _logger.LogWarning(
                    "Unable to analyze {Series} season {Season}: unable to fingerprint: {Ex}",
                    first.SeriesName,
                    first.SeasonNumber,
                    ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred during analysis");
                throw;
            }

            if (_config.RegenerateMediaSegments || (updateManagers && _config.UpdateMediaSegments))
            {
                await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync(episodes, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (_config.RegenerateMediaSegments)
        {
            _logger.LogInformation("Turning Mediasegment");
            _config.RegenerateMediaSegments = false;
            Plugin.Instance!.SaveConfiguration();
        }
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments.
    /// </summary>
    /// <param name="items">Media items to analyze.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of items that were successfully analyzed.</returns>
    private async Task<int> AnalyzeItems(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        AnalyzerAction action,
        CancellationToken cancellationToken)
    {
        var totalItems = items.Count(e => !e.GetAnalyzed(mode));

        // Only analyze specials (season 0) if the user has opted in.
        var first = items[0];
        if (!first.IsMovie && first.SeasonNumber == 0 && !_config.AnalyzeSeasonZero)
        {
            return 0;
        }

        _logger.LogInformation(
            "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}",
            mode,
            items.Count,
            first.SeriesName,
            first.SeasonNumber);

        var analyzers = new Collection<IMediaFileAnalyzer>();

        if (action == AnalyzerAction.Chapter || action == AnalyzerAction.Default)
        {
            analyzers.Add(new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>()));
        }

        if (first.IsAnime && !first.IsMovie && _config.WithChromaprint && !first.IsMovie && mode != AnalysisMode.Recap && mode != AnalysisMode.Preview && (action == AnalyzerAction.Chromaprint || action == AnalyzerAction.Default))
        {
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
        }

        if (mode == AnalysisMode.Credits && (action == AnalyzerAction.BlackFrame || action == AnalyzerAction.Default))
        {
            analyzers.Add(new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>()));
        }

        if (!first.IsAnime && !first.IsMovie && mode != AnalysisMode.Recap && mode != AnalysisMode.Preview && (action == AnalyzerAction.Chromaprint || action == AnalyzerAction.Default))
        {
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
        }

        // Use each analyzer to find skippable ranges in all media files, removing successfully
        // analyzed items from the queue.
        foreach (var analyzer in analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items = await analyzer.AnalyzeMediaFiles(items, mode, cancellationToken).ConfigureAwait(false);
        }

        // Add items without intros/credits to blacklist.
        var blacklisted = items.Where(e => !e.GetAnalyzed(mode)).ToList();
        await Plugin.Instance!.UpdateTimestamps(blacklisted.ToDictionary(e => e.EpisodeId, e => new Segment(e.EpisodeId)), mode).ConfigureAwait(false);
        totalItems -= blacklisted.Count;

        return totalItems;
    }
}
