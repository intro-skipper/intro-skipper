// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// <param name="logger">Logger.</param>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="providerManager">Provider manager.</param>
/// <param name="fileSystem">File system.</param>
public partial class CleanCacheTask(
    ILogger<CleanCacheTask> logger,
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem) : IScheduledTask
{
    private readonly ILogger<CleanCacheTask> _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly IFileSystem _fileSystem = fileSystem;

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
            _providerManager,
            _fileSystem);

        // QueueManager.GetMediaItems() already skips libraries where the plugin is disabled via
        // LibraryOptions.DisabledMediaSegmentProviders (same mechanism LegacyMigrations writes to).
        var queue = await queueManager.GetMediaItems(cancellationToken).ConfigureAwait(false);

        var enabledLibraryEpisodeIds = queue.Values
            .SelectMany(episodes => episodes.Select(e => e.EpisodeId))
            .ToHashSet();

        await plugin.CleanTimestampsAsync(enabledLibraryEpisodeIds, cancellationToken).ConfigureAwait(false);

        // Identify episode IDs in the SQLite cache that are no longer in enabled libraries.
        HashSet<Guid> invalidEpisodeIds;
        using (var cacheDb = Plugin.CreateCacheDb())
        {
            invalidEpisodeIds = cacheDb.GetAllEpisodeIds()
                .Where(id => !enabledLibraryEpisodeIds.Contains(id))
                .ToHashSet();
        }

        // Also sweep any legacy binary files still on disk (pre-migration installs).
        if (Directory.Exists(plugin.FingerprintCachePath))
        {
            using var cacheDb = Plugin.CreateCacheDb();
            foreach (var filePath in Directory.EnumerateFiles(plugin.FingerprintCachePath))
            {
                var filename = Path.GetFileName(filePath);
                var parts = filename.Split('-');
                if (parts.Length == 0 || !Guid.TryParse(parts[0], out var legacyId))
                {
                    continue;
                }

                if (!enabledLibraryEpisodeIds.Contains(legacyId))
                {
                    // Invalid episode — queue for full cache deletion via DeleteFingerprintCache.
                    invalidEpisodeIds.Add(legacyId);
                    continue;
                }

                // Valid episode — migrate binary data to the DB, then delete the disk file.
                var dbKey = GetMigratedDbKey(filename, legacyId.ToString("N"));
                if (dbKey is not null)
                {
                    try
                    {
                        LogMigratingLegacyFile(_logger, filePath);
                        cacheDb.Write(dbKey, await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false));
                    }
                    catch (IOException ex)
                    {
                        LogMigrationFailed(_logger, ex, filePath);
                    }
                }
                else
                {
                    // Non-migratable file (text-format fingerprint or stale detection format) — delete and let re-analysis repopulate.
                    LogDeletingNonMigratableLegacyFile(_logger, filePath);
                }

                try
                {
                    LogDeletingLegacyFile(_logger, filePath);
                    File.Delete(filePath);
                }
                catch (IOException ex)
                {
                    LogDeletingLegacyFileFailed(_logger, ex, filePath);
                }
            }

            // Remove the directory itself once it is empty (all legacy files migrated or deleted).
            if (!Directory.EnumerateFileSystemEntries(plugin.FingerprintCachePath).Any())
            {
                try
                {
                    Directory.Delete(plugin.FingerprintCachePath);
                }
                catch (IOException ex)
                {
                    LogDeletingLegacyDirectoryFailed(_logger, ex, plugin.FingerprintCachePath);
                }
            }
        }

        // Delete cache entries for invalid episode IDs (DB rows + any leftover files).
        foreach (var episodeId in invalidEpisodeIds)
        {
            LogDeletingCacheFiles(_logger, episodeId);
            FFmpegWrapper.DeleteFingerprintCache(episodeId);
        }

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting cache files for episode ID: {EpisodeId}")]
    private static partial void LogDeletingCacheFiles(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Migrating legacy cache file to DB: {FilePath}")]
    private static partial void LogMigratingLegacyFile(ILogger logger, string filePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to migrate legacy cache file '{FilePath}'")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception, string filePath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting stale legacy cache file: {FilePath}")]
    private static partial void LogDeletingLegacyFile(ILogger logger, string filePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete stale legacy cache file '{FilePath}'")]
    private static partial void LogDeletingLegacyFileFailed(ILogger logger, Exception exception, string filePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete legacy fingerprint cache directory '{DirectoryPath}'")]
    private static partial void LogDeletingLegacyDirectoryFailed(ILogger logger, Exception exception, string directoryPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting non-migratable legacy cache file: {FilePath}")]
    private static partial void LogDeletingNonMigratableLegacyFile(ILogger logger, string filePath);

    /// <summary>
    /// Maps a legacy on-disk cache filename to the current SQLite cache DB key.
    /// Returns <c>null</c> for text-format fingerprint files (too old to migrate binary-style).
    /// </summary>
    /// <param name="filename">The filename of the legacy cache file (without directory).</param>
    /// <param name="episodeIdN">The episode GUID formatted as 32 lowercase hex digits.</param>
    /// <returns>The DB key to use, or <c>null</c> if the file cannot be migrated.</returns>
    internal static string? GetMigratedDbKey(string filename, string episodeIdN)
    {
        // Plain GUID or GUID-credits: very old text-format fingerprint — delete without migrating.
        if (string.Equals(filename, episodeIdN, StringComparison.Ordinal) ||
            string.Equals(filename, episodeIdN + "-credits", StringComparison.Ordinal))
        {
            return null;
        }

        // blackframes and keyframes v1 → v2
        if ((filename.Contains("-blackframes-", StringComparison.Ordinal) ||
             filename.Contains("-keyframes-", StringComparison.Ordinal)) &&
            filename.EndsWith("-v1", StringComparison.Ordinal))
        {
            // Version bump represents a cache-format invalidation; copying raw bytes under the
            // new key would write old-format data that TryReadBinaryCache cannot deserialize.
            // Delete and let the next analysis repopulate in the current format.
            return null;
        }

        // silence v2 → v3
        if (filename.Contains("-silence-", StringComparison.Ordinal) &&
            filename.EndsWith("-v2", StringComparison.Ordinal))
        {
            return null;
        }

        // credits blackframes -alt → -v2
        if (filename.EndsWith("-alt", StringComparison.Ordinal))
        {
            return null;
        }

        // Already in current DB key format (chromaprint-v1, blackframes-v2, silence-v3, keyframes-v2, etc.)
        return filename;
    }
}
