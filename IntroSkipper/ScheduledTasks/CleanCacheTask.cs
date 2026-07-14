// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Clean the Intro Skipper cache of unused rows.
/// </summary>
/// <param name="logger">Logger.</param>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="providerManager">Provider manager.</param>
/// <param name="fileSystem">File system.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="mediaSegmentRefresher">Media segment refresher.</param>
public partial class CleanCacheTask(
    ILogger<CleanCacheTask> logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    IFFmpegService ffmpegService,
    IMediaSegmentRefresher mediaSegmentRefresher) : IScheduledTask
{
    private readonly ILogger<CleanCacheTask> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IMediaSegmentRefresher _mediaSegmentRefresher = mediaSegmentRefresher;

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
    public string Description => "Clear Intro Skipper cache of unused rows.";

    /// <summary>
    /// Gets the task key.
    /// </summary>
    public string Key => "CPBIntroSkipperCleanCache";

    /// <summary>
    /// Cleans the cache of unused rows.
    /// Clears segment rows that are no longer associated with episodes in the library.
    /// Clears season rows that are no longer associated with seasons in the library.
    /// </summary>
    /// <param name="progress">Task progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager,
            _providerManager,
            _fileSystem,
            _ffmpegService);

        // QueueManager.GetMediaItems() already skips libraries where the plugin is disabled via
        // LibraryOptions.DisabledMediaSegmentProviders.
        var queue = await queueManager.GetMediaItems(includeExcluded: true, cancellationToken).ConfigureAwait(false);
        var enabledLibraryEpisodes = queue.Values.SelectMany(static episodes => episodes).ToList();

        var enabledLibraryEpisodeIds = enabledLibraryEpisodes
            .Select(e => e.EpisodeId)
            .ToHashSet();

        var staleTimestampEpisodeIds = await plugin.CleanTimestampsAsync(enabledLibraryEpisodeIds, cancellationToken).ConfigureAwait(false);

        // The provider was disabled for these libraries, so the analyzer no longer refreshes
        // their Jellyfin segments. Refresh after deleting the plugin rows to remove any segments
        // that were previously synchronized by Intro Skipper.
        if (staleTimestampEpisodeIds.Count > 0 && plugin.Configuration.UpdateMediaSegments)
        {
            await _mediaSegmentRefresher.RefreshAsync(staleTimestampEpisodeIds, cancellationToken).ConfigureAwait(false);
        }

        // Identify episode IDs in the SQLite cache that are no longer in enabled libraries.
        HashSet<Guid> invalidEpisodeIds;
        using (var cacheDb = Plugin.CreateCacheDbContext())
        {
            invalidEpisodeIds = cacheDb.DetectionCache
                .Select(e => e.ItemId)
                .Distinct()
                .Where(id => !enabledLibraryEpisodeIds.Contains(id))
                .ToHashSet();
        }

        // Log and batch-delete all invalid episode DB rows in a single round-trip.
        foreach (var episodeId in invalidEpisodeIds)
        {
            LogDeletingDetectionCacheRows(_logger, episodeId);
        }

        if (invalidEpisodeIds.Count > 0)
        {
            try
            {
                using var deleteDb = Plugin.CreateCacheDbContext();
                await deleteDb.DetectionCache
                    .Where(e => invalidEpisodeIds.Contains(e.ItemId))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or DbException)
            {
                LogDeletingCacheRowsFailed(_logger, ex);
            }
        }

        // Clean up season state by removing items that no longer exist.
        await Plugin.CleanSeasonStateAsync(queue.Keys, cancellationToken).ConfigureAwait(false);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting detection cache rows for episode ID: {EpisodeId}")]
    private static partial void LogDeletingDetectionCacheRows(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to delete stale detection cache rows")]
    private static partial void LogDeletingCacheRowsFailed(ILogger logger, Exception exception);
}
