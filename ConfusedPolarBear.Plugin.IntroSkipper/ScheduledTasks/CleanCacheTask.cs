using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Analyze all television episodes for introduction sequences.
/// </summary>
public class CleanCacheTask : IScheduledTask
{
    private readonly ILogger<CleanCacheTask> _logger;

    private readonly ILoggerFactory _loggerFactory;

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanCacheTask"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public CleanCacheTask(
        ILogger<CleanCacheTask> logger,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the task name.
    /// </summary>
    public string Name => "Clean Intro Skipper Cache";

    /// <summary>
    /// Gets the task category.
    /// </summary>
    public string Category => "Intro Skipper";

    /// <summary>
    /// Gets the task description.
    /// </summary>
    public string Description => "Clear Intro Skipper cache of unused files.";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "CPBIntroSkipperCleanCache";

    /// <summary>
    /// Analyze all episodes in the queue. Only one instance of this task should be run at a time.
    /// </summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager);

        var queue = queueManager.GetMediaItems();

        var validEpisodeIds = new HashSet<Guid>();
        foreach (var seasonEpisodes in queue.Values)
        {
            foreach (var episode in seasonEpisodes)
            {
                validEpisodeIds.Add(episode.EpisodeId);
            }
        }

        // Delete invalid cache files
        foreach (string filePath in Directory.EnumerateFiles(Plugin.Instance!.FingerprintCachePath))
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);

            int dashIndex = fileName.IndexOf('-', StringComparison.Ordinal); // Find the index of the first '-' character
            if (dashIndex > 0)
            {
                fileName = fileName.Substring(0, dashIndex);
            }

            if (Guid.TryParse(fileName, out Guid episodeId))
            {
                if (!validEpisodeIds.Contains(episodeId))
                {
                    FFmpegWrapper.DeleteEpisodeCache(episodeId);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
