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
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Services;

internal static class CacheMigration
{
    internal const int CurrentCacheMigrationVersion = 1;

    private const string BlackFrameCacheExtension = ".ifbc";
    private const string SilenceCacheExtension = ".ifsc";

    private static readonly Regex LegacyFingerprintNameRegex = new(
        "^[0-9a-fA-F]{32}(-credits)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static async Task RunAsync(
        ILogger logger,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        if (Plugin.Instance is null)
        {
            throw new InvalidOperationException("Plugin instance was null");
        }

        var cachePath = Plugin.Instance.FingerprintCachePath;
        if (string.IsNullOrWhiteSpace(cachePath) || !Directory.Exists(cachePath))
        {
            logger.LogDebug("Fingerprint cache directory does not exist; nothing to migrate");
            return;
        }

        var queueManager = new QueueManager(
            loggerFactory.CreateLogger<QueueManager>(),
            libraryManager,
            providerManager,
            fileSystem);

        var queue = await queueManager.GetMediaItems(cancellationToken).ConfigureAwait(false);
        var validEpisodeIds = queue.Values
            .SelectMany(episodes => episodes.Select(e => e.EpisodeId))
            .ToHashSet();

        await Plugin.Instance.CleanTimestamps(validEpisodeIds).ConfigureAwait(false);

        var invalidEpisodeIds = Directory.EnumerateFiles(cachePath)
            .Select(filePath => Path.GetFileNameWithoutExtension(filePath).Split('-')[0])
            .Where(episodeIdStr => Guid.TryParseExact(episodeIdStr, "N", out var episodeId) && !validEpisodeIds.Contains(episodeId))
            .Select(episodeIdStr => Guid.ParseExact(episodeIdStr, "N"))
            .ToHashSet();

        foreach (var episodeId in invalidEpisodeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug("Deleting cache files for missing episode ID: {EpisodeId}", episodeId);
            FFmpegWrapper.DeleteEpisodeCache(episodeId);
        }

        var cacheFiles = Directory.EnumerateFiles(cachePath).ToArray();

        // Legacy fingerprint caches (text, no extension):
        //  - {EpisodeId:N}
        //  - {EpisodeId:N}-credits
        var legacyFingerprintFiles = cacheFiles
            .Where(p => string.IsNullOrEmpty(Path.GetExtension(p)))
            .Where(p => LegacyFingerprintNameRegex.IsMatch(Path.GetFileName(p)))
            .ToArray();

        var converted = 0;
        var deletedLegacyBecauseAlreadyConverted = 0;
        var skippedInvalid = 0;
        var failed = 0;

        foreach (var legacyPath in legacyFingerprintFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(legacyPath);
            var baseName = Path.GetFileNameWithoutExtension(legacyPath);
            var idPart = baseName.Split('-', 2)[0];
            if (!Guid.TryParseExact(idPart, "N", out var episodeId))
            {
                continue;
            }

            if (!validEpisodeIds.Contains(episodeId))
            {
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
                    logger.LogDebug("Legacy fingerprint cache was unreadable: {Path}", legacyPath);
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
                logger.LogDebug(ex, "Failed to convert legacy fingerprint cache {Path}", legacyPath);
            }
        }

        var (migratedBlackFrames, deletedBlackFrameLegacy, migratedSilence, deletedSilenceLegacy) =
            MigrateExtensionlessBinaryCaches(cachePath, cacheFiles, validEpisodeIds, logger, cancellationToken);

        await Plugin.Instance.CleanSeasonInfoAsync(queue.Keys).ConfigureAwait(false);

        Plugin.Instance.AnalyzeAgain = true;

        logger.LogInformation(
            "Startup cache migration complete. ConvertedFingerprint={Converted}, DeletedLegacyFingerprint={DeletedLegacy}, SkippedInvalid={SkippedInvalid}, FailedFingerprint={Failed}, MigratedBlackFrames={MigratedBlackFrames}, DeletedLegacyBlackFrames={DeletedBlackFrameLegacy}, MigratedSilence={MigratedSilence}, DeletedLegacySilence={DeletedSilenceLegacy}, RemovedMissingEpisodes={MissingEpisodes}",
            converted,
            deletedLegacyBecauseAlreadyConverted,
            skippedInvalid,
            failed,
            migratedBlackFrames,
            deletedBlackFrameLegacy,
            migratedSilence,
            deletedSilenceLegacy,
            invalidEpisodeIds.Count);
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
            var idPart = cacheKey.Split('-', 2)[0];
            if (!Guid.TryParseExact(idPart, "N", out var episodeId) || !validEpisodeIds.Contains(episodeId))
            {
                continue;
            }

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
}
