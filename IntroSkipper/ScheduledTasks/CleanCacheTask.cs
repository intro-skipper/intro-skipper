// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Clean the intro skipper cache of unused files.
/// </summary>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="providerManager">Provider manager.</param>
/// <param name="fileSystem">File system.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="detectionService">Media detection service.</param>
public class CleanCacheTask(
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    IDetectionCacheService cacheService,
    IMediaDetectionService detectionService) : IScheduledTask
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IDetectionCacheService _cacheService = cacheService;
    private readonly IMediaDetectionService _detectionService = detectionService;

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
    /// Cleans the cache of unused files.
    /// Clears the Segment cache by removing files that are no longer associated with episodes in the library.
    /// Clears the IgnoreList cache by removing items that are no longer associated with seasons in the library.
    /// </summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager,
            _providerManager,
            _fileSystem,
            _detectionService);

        // QueueManager.GetMediaItems() already skips libraries where the plugin is disabled via
        // LibraryOptions.DisabledMediaSegmentProviders (same mechanism LegacyMigrations writes to).
        var queue = await queueManager.GetMediaItems(cancellationToken).ConfigureAwait(false);
        var allEpisodes = queue.Values.SelectMany(static episodes => episodes).ToList();
        var enabledLibraryEpisodeIds = allEpisodes.Select(e => e.EpisodeId).ToHashSet();

        await plugin.CleanTimestampsAsync(enabledLibraryEpisodeIds, cancellationToken).ConfigureAwait(false);

        // Migrate any remaining legacy on-disk cache files before deleting stale entries,
        // so data is not lost if cleanup runs before any analysis task.
        await _cacheService.MigrateLegacyCachesAsync(allEpisodes, cancellationToken).ConfigureAwait(false);

        await _cacheService.DeleteStaleCachesAsync(enabledLibraryEpisodeIds, cancellationToken).ConfigureAwait(false);

        // Clean up Season information by removing items that are no longer exist.
        await plugin.CleanSeasonInfoAsync(queue.Keys, cancellationToken).ConfigureAwait(false);

        plugin.AnalyzeAgain = true;

        progress.Report(100);
    }

    /// <summary>
    /// Get task triggers.
    /// </summary>
    /// <returns>Task triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [];
    }
}
