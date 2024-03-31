namespace ConfusedPolarBear.Plugin.IntroSkipper;

using System;
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
    private readonly AnalysisMode _analysisMode;

    private readonly ILogger _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseItemAnalyzerTask"/> class.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="logger">Task logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    public BaseItemAnalyzerTask(
        AnalysisMode mode,
        ILogger logger,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager)
    {
        _analysisMode = mode;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;

        if (mode == AnalysisMode.Introduction)
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

        if (totalQueued == 0)
        {
            throw new FingerprintException(
                "No episodes to analyze. If you are limiting the list of libraries to analyze, check that all library names have been spelled correctly.");
        }

        if (this._analysisMode == AnalysisMode.Introduction)
        {
            EdlManager.LogConfiguration();
        }

        var totalProcessed = 0;
        var options = new ParallelOptions()
        {
            MaxDegreeOfParallelism = Plugin.Instance!.Configuration.MaxParallelism
        };

        Parallel.ForEach(queue, options, (season) =>
        {
            var writeEdl = false;

            // Since the first run of the task can run for multiple hours, ensure that none
            // of the current media items were deleted from Jellyfin since the task was started.
            var (episodes, unanalyzed) = queueManager.VerifyQueue(
                season.Value.AsReadOnly(),
                this._analysisMode);

            if (episodes.Count == 0)
            {
                return;
            }

            var first = episodes[0];

            if (!unanalyzed)
            {
                _logger.LogDebug(
                    "All episodes in {Name} season {Season} have already been analyzed",
                    first.SeriesName,
                    first.SeasonNumber);

                return;
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var analyzed = AnalyzeItems(episodes, cancellationToken);
                Interlocked.Add(ref totalProcessed, analyzed);

                writeEdl = analyzed > 0 || Plugin.Instance!.Configuration.RegenerateEdlFiles;
            }
            catch (FingerprintException ex)
            {
                _logger.LogWarning(
                    "Unable to analyze {Series} season {Season}: unable to fingerprint: {Ex}",
                    first.SeriesName,
                    first.SeasonNumber,
                    ex);
            }

            if (
                writeEdl &&
                Plugin.Instance!.Configuration.EdlAction != EdlAction.None &&
                _analysisMode == AnalysisMode.Introduction)
            {
                EdlManager.UpdateEDLFiles(episodes);
            }

            if (_analysisMode == AnalysisMode.Introduction)
            {
                progress.Report(((totalProcessed * 100) / totalQueued) / 2);
            }
            else
            {
                progress.Report((((totalProcessed * 100) / totalQueued) / 2) + 50);
            }
        });

        if (
            _analysisMode == AnalysisMode.Introduction &&
            Plugin.Instance!.Configuration.RegenerateEdlFiles)
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of items that were successfully analyzed.</returns>
    private int AnalyzeItems(
        ReadOnlyCollection<QueuedEpisode> items,
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
            "Analyzing {Count} files from {Name} season {Season}",
            items.Count,
            first.SeriesName,
            first.SeasonNumber);

        var analyzers = new Collection<IMediaFileAnalyzer>();

        analyzers.Add(new ChapterAnalyzer(_loggerFactory.CreateLogger<ChapterAnalyzer>()));
        if (Plugin.Instance!.Configuration.UseChromaprint)
        {
            analyzers.Add(new ChromaprintAnalyzer(_loggerFactory.CreateLogger<ChromaprintAnalyzer>()));
        }

        if (this._analysisMode == AnalysisMode.Credits)
        {
            analyzers.Add(new BlackFrameAnalyzer(_loggerFactory.CreateLogger<BlackFrameAnalyzer>()));
        }

        // Use each analyzer to find skippable ranges in all media files, removing successfully
        // analyzed items from the queue.
        foreach (var analyzer in analyzers)
        {
            items = analyzer.AnalyzeMediaFiles(items, this._analysisMode, cancellationToken);
        }

        return totalItems;
    }
}
