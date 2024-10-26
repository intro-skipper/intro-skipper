// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
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

    /// <summary>
    /// Analyze all media items on the server.
    /// </summary>
    /// <param name="progress">Progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="modes">Modes.</param>
    /// <param name="seasonsToAnalyze">Season Ids to analyze.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task AnalyzeItems(
        IProgress<double> progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<AnalysisMode>? modes = null,
        IReadOnlyCollection<Guid>? seasonsToAnalyze = null)
    {
        var ffmpegValid = FFmpegWrapper.CheckFFmpegVersion();
        // Assert that ffmpeg with chromaprint is installed
        if (Plugin.Instance!.Configuration.WithChromaprint && !ffmpegValid)
        {
            throw new FingerprintException(
                "Analysis terminated! Chromaprint is not enabled in the current ffmpeg. If Jellyfin is running natively, install jellyfin-ffmpeg5. If Jellyfin is running in a container, upgrade to version 10.8.0 or newer.");
        }

        modes ??= [AnalysisMode.Introduction, AnalysisMode.Credits, AnalysisMode.Recap, AnalysisMode.Preview];

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
            MaxDegreeOfParallelism = Plugin.Instance.Configuration.MaxParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(queue, options, async (season, ct) =>
        {
            var updateManagers = false;

            // Since the first run of the task can run for multiple hours, ensure that none
            // of the current media items were deleted from Jellyfin since the task was started.
            var (episodes, requiredModes) = queueManager.VerifyQueue(
                season.Value,
                modes.Where(m => !Plugin.Instance!.IsIgnored(season.Key, m)).ToList());

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
                    var analyzed = AnalyzeItems(episodes, mode, ct);
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

            if (Plugin.Instance.Configuration.RegenerateMediaSegments || (updateManagers && Plugin.Instance.Configuration.UpdateMediaSegments))
            {
                await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync(episodes, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        if (Plugin.Instance.Configuration.RegenerateMediaSegments)
        {
            _logger.LogInformation("Turning Mediasegment");
            Plugin.Instance.Configuration.RegenerateMediaSegments = false;
            Plugin.Instance.SaveConfiguration();
        }
    }

    /// <summary>
    /// Analyze a group of media items for skippable segments.
    /// </summary>
    /// <param name="items">Media items to analyze.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of items that were successfully analyzed.</returns>
    private int AnalyzeItems(
        IReadOnlyList<QueuedEpisode> items,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var totalItems = items.Count(e => !e.State.IsAnalyzed(mode));

        // Only analyze specials (season 0) if the user has opted in.
        var first = items[0];
        if (!first.IsMovie && first.SeasonNumber == 0 && !Plugin.Instance!.Configuration.AnalyzeSeasonZero)
        {
            return 0;
        }

        // Remove from Blacklist
        foreach (var item in items.Where(e => e.State.IsBlacklisted(mode)))
        {
            item.State.SetBlacklisted(mode, false);
        }

        _logger.LogInformation(
            "[Mode: {Mode}] Analyzing {Count} files from {Name} season {Season}",
            mode,
            items.Count,
            first.SeriesName,
            first.SeasonNumber);

        var analyzers = new Collection<IMediaFileAnalyzer>
        {
            new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>())
        };

        if (first.IsAnime && Plugin.Instance!.Configuration.WithChromaprint && !first.IsMovie && mode != AnalysisMode.Recap && mode != AnalysisMode.Preview)
        {
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
        }

        if (mode == AnalysisMode.Credits)
        {
            analyzers.Add(new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>()));
        }

        if (!first.IsAnime && Plugin.Instance!.Configuration.WithChromaprint && !first.IsMovie && mode != AnalysisMode.Recap && mode != AnalysisMode.Preview)
        {
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
        }

        // Use each analyzer to find skippable ranges in all media files, removing successfully
        // analyzed items from the queue.
        foreach (var analyzer in analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items = analyzer.AnalyzeMediaFiles(items, mode, cancellationToken);
        }

        // Add items without intros/credits to blacklist.
        foreach (var item in items.Where(e => !e.State.IsAnalyzed(mode)))
        {
            item.State.SetBlacklisted(mode, true);
            totalItems -= 1;
        }

        return totalItems;
    }
}
