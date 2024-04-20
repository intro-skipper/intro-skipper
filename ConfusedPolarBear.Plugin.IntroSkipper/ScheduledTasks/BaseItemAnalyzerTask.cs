namespace ConfusedPolarBear.Plugin.IntroSkipper;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

/// <summary>
/// Common code shared by all media item analyzer tasks.
/// </summary>
public class BaseItemAnalyzerTask
{
    private readonly ReadOnlyCollection<AnalysisMode> _analysisModes;

    private readonly ILogger _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseItemAnalyzerTask"/> class.
    /// </summary>
    /// <param name="modes">Analysis mode.</param>
    /// <param name="logger">Task logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    public BaseItemAnalyzerTask(
        ReadOnlyCollection<AnalysisMode> modes,
        ILogger logger,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager)
    {
        _analysisModes = modes;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;

        if (Plugin.Instance!.Configuration.EdlAction != EdlAction.None)
        {
            EdlManager.Initialize(_logger);
        }
    }

    /// <summary>
    /// Analyze all media items on the server.
    /// </summary>
    /// <param name="progress">Progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void AnalyzeItems(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var ffmpegValid = FFmpegWrapper.CheckFFmpegVersion();
        // Assert that ffmpeg with chromaprint is installed
        if (Plugin.Instance!.Configuration.UseChromaprint && !ffmpegValid)
        {
            throw new FingerprintException(
                "Analysis terminated! Chromaprint is not enabled in the current ffmpeg. If Jellyfin is running natively, install jellyfin-ffmpeg5. If Jellyfin is running in a container, upgrade to version 10.8.0 or newer.");
        }

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager);

        var queue = queueManager.GetMediaItems();

        var totalQueued = 0;
        foreach (var kvp in queue)
        {
            totalQueued += kvp.Value.Count;
        }

        totalQueued *= _analysisModes.Count;

        if (totalQueued == 0)
        {
            throw new FingerprintException(
                "No episodes to analyze. If you are limiting the list of libraries to analyze, check that all library names have been spelled correctly.");
        }

        if (Plugin.Instance!.Configuration.EdlAction != EdlAction.None)
        {
            EdlManager.LogConfiguration();
        }

        var totalProcessed = 0;
        var modeCount = _analysisModes.Count;
        var options = new ParallelOptions()
        {
            MaxDegreeOfParallelism = Plugin.Instance!.Configuration.MaxParallelism
        };

        Parallel.ForEach(queue, options, (season) =>
        {
            var writeEdl = false;

            // Since the first run of the task can run for multiple hours, ensure that none
            // of the current media items were deleted from Jellyfin since the task was started.
            var (episodes, requiredModes) = queueManager.VerifyQueue(
                season.Value.AsReadOnly(),
                _analysisModes);

            var episodeCount = episodes.Count;

            if (episodeCount == 0)
            {
                return;
            }

            var first = episodes[0];
            var requiredModeCount = requiredModes.Count;

            if (requiredModeCount == 0) 
            {
                _logger.LogDebug(
                    "All episodes in {Name} season {Season} have already been analyzed",
                    first.SeriesName,
                    first.SeasonNumber);
                    
                Interlocked.Add(ref totalProcessed, (episodeCount * modeCount)); // Update totalProcessed directly
                progress.Report((totalProcessed * 100) / totalQueued);

                return;
            }

            if (modeCount != requiredModeCount)
            {
                Interlocked.Add(ref totalProcessed, episodeCount);
                progress.Report((totalProcessed * 100) / totalQueued); // Partial analysis some modes have already been analyzed
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                foreach (AnalysisMode mode in requiredModes)
                {
                    var analyzed = AnalyzeItems(episodes, mode, cancellationToken);
                    Interlocked.Add(ref totalProcessed, analyzed);

                    writeEdl = analyzed > 0 || Plugin.Instance!.Configuration.RegenerateEdlFiles;

                    progress.Report((totalProcessed * 100) / totalQueued);
                }
            }
            catch (FingerprintException ex)
            {
                _logger.LogWarning(
                    "Unable to analyze {Series} season {Season}: unable to fingerprint: {Ex}",
                    first.SeriesName,
                    first.SeasonNumber,
                    ex);
            }

            if (writeEdl && Plugin.Instance!.Configuration.EdlAction != EdlAction.None)
            {
                EdlManager.UpdateEDLFiles(episodes);
            }
        });

        if (Plugin.Instance!.Configuration.RegenerateEdlFiles)
        {
            _logger.LogInformation("Turning EDL file regeneration flag off");
            Plugin.Instance!.Configuration.RegenerateEdlFiles = false;
            Plugin.Instance!.SaveConfiguration();
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
        ReadOnlyCollection<QueuedEpisode> items,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        var totalItems = items.Count;

        // Only analyze specials (season 0) if the user has opted in.
        var first = items[0];
        if (first.SeasonNumber == 0 && !Plugin.Instance!.Configuration.AnalyzeSeasonZero)
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

        analyzers.Add(new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>()));
        if (Plugin.Instance!.Configuration.UseChromaprint)
        {
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
        }

        if (mode == AnalysisMode.Credits)
        {
            analyzers.Add(new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>()));
        }

        // Use each analyzer to find skippable ranges in all media files, removing successfully
        // analyzed items from the queue.
        foreach (var analyzer in analyzers)
        {
            items = analyzer.AnalyzeMediaFiles(items, mode, cancellationToken);
        }

        return totalItems;
    }
}
