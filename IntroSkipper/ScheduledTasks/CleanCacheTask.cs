// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Manager;
using IntroSkipper.Repositories;
using IntroSkipper.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Analyze all television episodes for introduction sequences.
/// </summary>
public partial class CleanCacheTask : IScheduledTask
{
    private readonly ILogger<CleanCacheTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanCacheTask"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="serviceProvider">Service provider.</param>
    public CleanCacheTask(
        ILogger<CleanCacheTask> logger,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
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

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance was null");

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager,
            _serviceProvider);

        // QueueManager.GetMediaItems() already skips libraries where the plugin is disabled via
        // LibraryOptions.DisabledMediaSegmentProviders (same mechanism LegacyMigrations writes to).
        var queue = await queueManager.GetMediaItems(cancellationToken).ConfigureAwait(false);

        var enabledLibraryEpisodeIds = queue.Values
            .SelectMany(episodes => episodes.Select(e => e.EpisodeId))
            .ToHashSet();

        using (var scope = _serviceProvider.CreateScope())
        {
            var segmentService = scope.ServiceProvider.GetRequiredService<ISegmentService>();
            await segmentService.CleanupOrphanedSegmentsAsync(enabledLibraryEpisodeIds, cancellationToken).ConfigureAwait(false);
        }

        // Identify episode IDs with cached files that are no longer in enabled libraries
        var invalidEpisodeIds = Directory.EnumerateFiles(plugin.FingerprintCachePath)
            .Select(filePath => Path.GetFileNameWithoutExtension(filePath).Split('-')[0])
            .Where(episodeIdStr => Guid.TryParse(episodeIdStr, out var episodeId) && !enabledLibraryEpisodeIds.Contains(episodeId))
            .Select(Guid.Parse)
            .ToHashSet();

        // Delete cache files for invalid episode IDs
        foreach (var episodeId in invalidEpisodeIds)
        {
            LogDeletingCacheFiles(_logger, episodeId);
            FFmpegWrapper.DeleteFingerprintCache(episodeId);
        }

        // Clean up Season information by removing items that are no longer exist.
        using (var scope = _serviceProvider.CreateScope())
        {
            var seasonRepository = scope.ServiceProvider.GetRequiredService<ISeasonRepository>();
            await seasonRepository.DeleteOrphanedSeasonsAsync(queue.Keys, cancellationToken).ConfigureAwait(false);
        }

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting cache files for episode ID: {EpisodeId}")]
    private static partial void LogDeletingCacheFiles(ILogger logger, Guid episodeId);
}
