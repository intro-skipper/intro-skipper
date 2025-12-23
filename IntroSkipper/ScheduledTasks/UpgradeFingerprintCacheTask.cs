// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.ScheduledTasks;

/// <summary>
/// Converts legacy (text) fingerprint cache files to the binary <c>.ifch</c> format
/// and deletes cache entries whose source items no longer exist.
/// </summary>
public class UpgradeFingerprintCacheTask : IScheduledTask
{
    private const int CurrentCacheMigrationVersion = 1;
    private const string BlackFrameCacheExtension = ".ifbc";
    private const string SilenceCacheExtension = ".ifsc";

    private static readonly Regex LegacyFingerprintNameRegex = new(
        "^[0-9a-fA-F]{32}(-credits)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<UpgradeFingerprintCacheTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpgradeFingerprintCacheTask"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    public UpgradeFingerprintCacheTask(
        ILogger<UpgradeFingerprintCacheTask> logger,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public string Name => "One Time Startup Upgrade Fingerprint Cache";

    /// <inheritdoc />
    public string Category => "Intro Skipper";

    /// <inheritdoc />
    public string Description => "Converts legacy fingerprint cache files to the binary format and removes cache entries for missing media.";

    /// <inheritdoc />
    public string Key => "IntroSkipperUpgradeFingerprintCache";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

        if (Plugin.Instance is null)
        {
            throw new InvalidOperationException("Plugin instance was null");
        }

        // Run this migration only once per version. If we ever add a new migration step,
        // bump CurrentCacheMigrationVersion.
        if (Plugin.Instance.Configuration.CacheMigrationVersion >= CurrentCacheMigrationVersion)
        {
            _logger.LogDebug(
                "Fingerprint cache migration already completed (CacheMigrationVersion={CacheMigrationVersion}); skipping",
                Plugin.Instance.Configuration.CacheMigrationVersion);
            progress.Report(100);
            return;
        }

        var cachePath = Plugin.Instance.FingerprintCachePath;
        if (string.IsNullOrWhiteSpace(cachePath) || !Directory.Exists(cachePath))
        {
            _logger.LogDebug("Fingerprint cache directory does not exist; nothing to do");

            Plugin.Instance.Configuration.CacheMigrationVersion = CurrentCacheMigrationVersion;
            Plugin.Instance.SaveConfiguration();

            progress.Report(100);
            return;
        }

        // Fresh installs will have an empty cache directory. In that case we can immediately
        // mark the migration as completed and skip the expensive library/queue scan.
        var cacheFiles = Directory.EnumerateFiles(cachePath).ToArray();
        if (cacheFiles.Length == 0)
        {
            _logger.LogDebug("Fingerprint cache directory is empty; nothing to migrate");

            Plugin.Instance.Configuration.CacheMigrationVersion = CurrentCacheMigrationVersion;
            Plugin.Instance.SaveConfiguration();

            progress.Report(100);
            return;
        }

        var queueManager = new QueueManager(
            _loggerFactory.CreateLogger<QueueManager>(),
            _libraryManager,
            _providerManager,
            _fileSystem);

        // Retrieve media items and get valid episode IDs.
        var queue = await queueManager.GetMediaItems(cancellationToken).ConfigureAwait(false);
        var validEpisodeIds = queue.Values
            .SelectMany(episodes => episodes.Select(e => e.EpisodeId))
            .ToHashSet();

        // Delete timestamps for items that no longer exist.
        await Plugin.Instance.CleanTimestamps(validEpisodeIds).ConfigureAwait(false);

        // Identify invalid episode IDs based on cache folder contents.
        var invalidEpisodeIds = cacheFiles
            .Select(filePath => Path.GetFileNameWithoutExtension(filePath).Split('-')[0])
            .Where(episodeIdStr => Guid.TryParseExact(episodeIdStr, "N", out var episodeId) && !validEpisodeIds.Contains(episodeId))
            .Select(episodeIdStr => Guid.ParseExact(episodeIdStr, "N"))
            .ToHashSet();

        // Delete cache files for invalid episode IDs.
        foreach (var episodeId in invalidEpisodeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Deleting cache files for missing episode ID: {EpisodeId}", episodeId);
            FFmpegWrapper.DeleteEpisodeCache(episodeId);
        }

        // Convert legacy fingerprint caches (text) to binary (.ifch) for remaining items.
        // Legacy fingerprint cache files:
        //  - {EpisodeId:N}
        //  - {EpisodeId:N}-credits
        var legacyFingerprintFiles = cacheFiles
            .Where(p => string.IsNullOrEmpty(Path.GetExtension(p)))
            .Where(p => LegacyFingerprintNameRegex.IsMatch(Path.GetFileName(p)))
            .ToArray();

        var totalLegacy = legacyFingerprintFiles.Length;
        var converted = 0;
        var deletedLegacyBecauseAlreadyConverted = 0;
        var skippedInvalid = 0;
        var failed = 0;

        for (var i = 0; i < legacyFingerprintFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var legacyPath = legacyFingerprintFiles[i];
            var fileName = Path.GetFileName(legacyPath);

            var baseName = Path.GetFileNameWithoutExtension(legacyPath);
            var idPart = baseName.Split('-', 2)[0];
            if (!Guid.TryParseExact(idPart, "N", out var episodeId))
            {
                continue;
            }

            if (!validEpisodeIds.Contains(episodeId))
            {
                // Might have been missed by invalidEpisodeIds if the cache directory is changing during the run.
                skippedInvalid++;
                continue;
            }

            var mode = fileName.EndsWith("-credits", StringComparison.OrdinalIgnoreCase)
                ? AnalysisMode.Credits
                : AnalysisMode.Introduction;

            var episode = new QueuedEpisode { EpisodeId = episodeId };
            var targetPath = FFmpegWrapper.GetFingerprintCachePath(episode, mode, cachePath);

            if (File.Exists(targetPath))
            {
                TryDeleteLegacyArtifacts(legacyPath);
                deletedLegacyBecauseAlreadyConverted++;
                continue;
            }

            try
            {
                using var stream = File.Open(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (!FFmpegWrapper.TryReadLegacyFingerprint(stream, out var legacyFingerprint))
                {
                    failed++;
                    _logger.LogDebug("Legacy fingerprint cache was unreadable: {Path}", legacyPath);
                    continue;
                }

                FFmpegWrapper.CacheFingerprint(
                    episode,
                    mode,
                    legacyFingerprint.ToList(),
                    cacheDirectoryOverride: cachePath);

                converted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                _logger.LogDebug(ex, "Failed to convert legacy fingerprint cache {Path}", legacyPath);
            }

            // Report progress: cleanup phase is fast; conversion is the main part.
            if (totalLegacy > 0)
            {
                var pct = (i + 1) / (double)totalLegacy;
                progress.Report(Math.Min(100, pct * 100));
            }
        }

        // Migrate binary caches that historically lacked an extension.
        // This includes blackframe and silence caches (which already have a binary structure)
        // but does NOT include other cached outputs (e.g., keyframe/showinfo logs).
        var (migratedBlackFrames, deletedBlackFrameLegacy, migratedSilence, deletedSilenceLegacy) =
            MigrateExtensionlessBinaryCaches(cachePath, cacheFiles, validEpisodeIds, _logger, cancellationToken);

        // Clean up Season information by removing items that no longer exist.
        await Plugin.Instance.CleanSeasonInfoAsync(queue.Keys).ConfigureAwait(false);

        Plugin.Instance.AnalyzeAgain = true;

        _logger.LogInformation(
            "Cache upgrade complete. LegacyFingerprint={LegacyTotal}, ConvertedFingerprint={Converted}, DeletedLegacyFingerprint={DeletedLegacy}, SkippedInvalid={SkippedInvalid}, FailedFingerprint={Failed}, MigratedBlackFrames={MigratedBlackFrames}, DeletedLegacyBlackFrames={DeletedBlackFrameLegacy}, MigratedSilence={MigratedSilence}, DeletedLegacySilence={DeletedSilenceLegacy}, RemovedMissingEpisodes={MissingEpisodes}",
            totalLegacy,
            converted,
            deletedLegacyBecauseAlreadyConverted,
            skippedInvalid,
            failed,
            migratedBlackFrames,
            deletedBlackFrameLegacy,
            migratedSilence,
            deletedSilenceLegacy,
            invalidEpisodeIds.Count);

        Plugin.Instance.Configuration.CacheMigrationVersion = CurrentCacheMigrationVersion;
        Plugin.Instance.SaveConfiguration();

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger,
            }
        ];
    }

    private static void TryDeleteLegacyArtifacts(string legacyPath)
    {
        TryDeleteFile(legacyPath);
        TryDeleteFile(legacyPath + ".tmp");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    internal static (int MigratedBlackFrames, int DeletedBlackFrameLegacy, int MigratedSilence, int DeletedSilenceLegacy)
        MigrateExtensionlessBinaryCaches(
            string cachePath,
            IReadOnlyList<string> cacheFiles,
            IReadOnlySet<Guid> validEpisodeIds,
            ILogger logger,
            CancellationToken cancellationToken)
    {
        var migratedBlackFrames = 0;
        var deletedBlackFrameLegacy = 0;
        var migratedSilence = 0;
        var deletedSilenceLegacy = 0;

        var extensionlessNonFingerprintFiles = cacheFiles
            .Where(p => string.IsNullOrEmpty(Path.GetExtension(p)))
            .Where(p => !LegacyFingerprintNameRegex.IsMatch(Path.GetFileName(p)))
            .ToArray();

        foreach (var filePath in extensionlessNonFingerprintFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cacheKey = Path.GetFileName(filePath);

            // Only consider cache keys that belong to a specific episode.
            var idPart = cacheKey.Split('-', 2)[0];
            if (!Guid.TryParseExact(idPart, "N", out var episodeId) || !validEpisodeIds.Contains(episodeId))
            {
                continue;
            }

            // Blackframe cache migration.
            if (cacheKey.Contains("blackframes", StringComparison.OrdinalIgnoreCase) &&
                FFmpegWrapper.TryLoadBlackFrameCache(cacheKey, out _, cachePath))
            {
                var target = filePath + BlackFrameCacheExtension;
                if (File.Exists(target))
                {
                    TryDeleteLegacyArtifacts(filePath);
                    deletedBlackFrameLegacy++;
                }
                else
                {
                    try
                    {
                        File.Move(filePath, target, overwrite: true);
                        TryDeleteFile(filePath + ".tmp");
                        migratedBlackFrames++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogDebug(ex, "Failed to migrate blackframe cache file {Path}", filePath);
                    }
                }

                continue;
            }

            // Silence cache migration.
            if (cacheKey.Contains("silence", StringComparison.OrdinalIgnoreCase) &&
                FFmpegWrapper.TryLoadSilenceCache(cacheKey, out _, cachePath))
            {
                var target = filePath + SilenceCacheExtension;
                if (File.Exists(target))
                {
                    TryDeleteLegacyArtifacts(filePath);
                    deletedSilenceLegacy++;
                }
                else
                {
                    try
                    {
                        File.Move(filePath, target, overwrite: true);
                        TryDeleteFile(filePath + ".tmp");
                        migratedSilence++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogDebug(ex, "Failed to migrate silence cache file {Path}", filePath);
                    }
                }
            }
        }

        return (migratedBlackFrames, deletedBlackFrameLegacy, migratedSilence, deletedSilenceLegacy);
    }
}
