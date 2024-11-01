// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

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
    /// Cleans the cache of unused files.
    /// Clears the Segment cache by removing files that are no longer associated with episodes in the library.
    /// Clears the IgnoreList cache by removing items that are no longer associated with seasons in the library.
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

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager);

        // Retrieve media items and get valid episode IDs
        var queue = queueManager.GetMediaItems();
        var validEpisodeIds = new HashSet<Guid>(queue.Values.SelectMany(episodes => episodes.Select(e => e.EpisodeId)));

        await Plugin.Instance!.CleanTimestamps(validEpisodeIds).ConfigureAwait(false);

        // Identify invalid episode IDs
        var invalidEpisodeIds = Directory.EnumerateFiles(Plugin.Instance!.FingerprintCachePath)
            .Select(filePath =>
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var episodeIdStr = fileName.Split('-')[0];
                if (Guid.TryParse(episodeIdStr, out Guid episodeId))
                {
                    return validEpisodeIds.Contains(episodeId) ? (Guid?)null : episodeId;
                }

                return null;
            })
            .OfType<Guid>()
            .ToHashSet();

        // Delete cache files for invalid episode IDs
        foreach (var episodeId in invalidEpisodeIds)
        {
            _logger.LogDebug("Deleting cache files for episode ID: {EpisodeId}", episodeId);
            FFmpegWrapper.DeleteEpisodeCache(episodeId);
        }

        // Clean up ignore list by removing items that are no longer exist..
        var removedItems = false;
        foreach (var ignoredItem in Plugin.Instance.IgnoreList.Values.ToList())
        {
            if (!queue.ContainsKey(ignoredItem.SeasonId))
            {
                removedItems = true;
                Plugin.Instance.IgnoreList.TryRemove(ignoredItem.SeasonId, out _);
            }
        }

        // Save ignore list if at least one item was removed.
        if (removedItems)
        {
            try
            {
                Plugin.Instance!.SaveIgnoreList();
            }
            catch (Exception e)
            {
                _logger.LogError("Failed to save ignore list: {Error}", e.Message);
            }
        }

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
