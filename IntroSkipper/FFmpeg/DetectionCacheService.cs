// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2022 nyanmisaka
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LegacyCacheKind = IntroSkipper.FFmpeg.LegacyDetectionCacheFileName.LegacyCacheKind;
using LegacyParseResult = IntroSkipper.FFmpeg.LegacyDetectionCacheFileName.LegacyParseResult;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides detection-cache operations backed by a SQLite database and legacy on-disk text files.
/// </summary>
public sealed partial class DetectionCacheService : IDetectionCacheService
{
    /// <summary>
    /// Epsilon for floating-point cache-key lookups. Using a tolerance instead of exact
    /// equality avoids misses caused by floating-point round-trip through SQLite storage.
    /// </summary>
    private const double CacheTimeTolerance = 1e-6;

    private readonly IPluginOptionsProvider _options;
    private readonly ILogger<DetectionCacheService> _logger;

    /// <summary>
    /// Fast-path guard, not a mutex. Concurrent callers may both enter the migration body,
    /// which is safe because all operations inside (upserts, file deletes) are idempotent.
    /// </summary>
    private volatile bool _legacyMigrationCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheService"/> class.
    /// </summary>
    /// <param name="options">Options provider for cache configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public DetectionCacheService(
        IPluginOptionsProvider options,
        ILogger<DetectionCacheService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<T[]?> TryReadJsonCacheAsync<T>(DetectionCacheKey key, CancellationToken cancellationToken = default)
    {
        if (!_options.CacheFingerprints)
        {
            return null;
        }

        try
        {
            using var db = Plugin.CreateCacheDbContext();

            var entry = await WhereCacheKey(db.DetectionCache, key)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                return null;
            }

            var json = DecompressBrotli<T[]>(entry.Data) ?? [];

            LogDetectionCacheHit(_logger, GetCacheLogKey(key));

            return json;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidDataException or DbException)
        {
            LogDetectionCacheReadError(_logger, ex, GetCacheLogKey(key));

            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> WriteJsonCacheAsync<T>(DetectionCacheKey key, T[] items, CancellationToken cancellationToken = default)
    {
        if (!_options.CacheFingerprints)
        {
            return false;
        }

        var data = CompressBrotli(items);

        try
        {
            using var db = Plugin.CreateCacheDbContext();

            await UpsertJsonCacheEntryAsync(db, key.ItemId, key.Mode, key.Type, key.Start, key.End, key.Variant, data, cancellationToken)
                .ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogDetectionCacheWriteError(_logger, ex, GetCacheLogKey(key));

            // Suppress duplicate-insert races and database-level cache failures. The cache is a
            // performance optimization; write failures should never discard valid analysis results.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task DeleteFingerprintCacheAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            // Delete from the SQLite cache database.
            using var db = Plugin.CreateCacheDbContext();
            await db.DetectionCache.Where(e => e.ItemId == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogDetectionCacheDeleteError(_logger, ex, id.ToString("N"));
        }

        var cacheDir = _options.FingerprintCachePath;
        if (cacheDir is not null && Directory.Exists(cacheDir))
        {
            var filePattern = id.ToString("N") + "*";
            foreach (var filePath in Directory.EnumerateFiles(cacheDir, filePattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogDeleteEpisodeCache(_logger, filePath);

                try
                {
                    File.Delete(filePath);
                }
                catch (IOException ex)
                {
                    LogDeleteLegacyCacheFileFailed(_logger, ex, filePath);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task DeleteCacheFilesAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        try
        {
            // Delete from the SQLite cache database.
            using var db = Plugin.CreateCacheDbContext();
            await db.DetectionCache.Where(e => e.Mode == mode)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogDetectionCacheDeleteError(_logger, ex, mode.ToString());
        }

        // Legacy on-disk cache files only existed for Introduction and Credits modes.
        // Other modes (Preview, Recap, Commercial) never produced legacy files.
        var cacheDir = _options.FingerprintCachePath;
        if (cacheDir is not null && Directory.Exists(cacheDir) &&
            mode is AnalysisMode.Introduction or AnalysisMode.Credits)
        {
            foreach (var filePath in Directory.EnumerateFiles(cacheDir)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    var isCreditOrBlackframe = name.Contains("credit", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("blackframes", StringComparison.OrdinalIgnoreCase);
                    return mode == AnalysisMode.Introduction ? !isCreditOrBlackframe : isCreditOrBlackframe;
                }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(filePath);
                }
                catch (IOException ex)
                {
                    LogDeleteLegacyCacheFileFailed(_logger, ex, filePath);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task DeleteStaleCachesAsync(IReadOnlySet<Guid> enabledItemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enabledItemIds);

        HashSet<Guid> invalidItemIds;
        try
        {
            using var db = Plugin.CreateCacheDbContext();
            invalidItemIds = [.. await db.DetectionCache
                .Select(e => e.ItemId)
                .Distinct()
                .Where(id => !enabledItemIds.Contains(id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false)];
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogDetectionCacheDeleteError(_logger, ex, "stale-detection-cache-scan");
            invalidItemIds = [];
        }

        var staleLegacyFiles = new List<string>();
        var cacheDir = _options.FingerprintCachePath;
        if (!string.IsNullOrEmpty(cacheDir) && Directory.Exists(cacheDir))
        {
            List<string> legacyFiles;
            try
            {
                legacyFiles = [.. Directory.EnumerateFiles(cacheDir)];
            }
            catch (DirectoryNotFoundException)
            {
                legacyFiles = [];
            }

            foreach (var filePath in legacyFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filename = Path.GetFileName(filePath);
                var legacyFile = LegacyDetectionCacheFileName.TryParse(filename);
                if (legacyFile is null || enabledItemIds.Contains(legacyFile.ItemId))
                {
                    continue;
                }

                invalidItemIds.Add(legacyFile.ItemId);
                staleLegacyFiles.Add(filePath);
            }
        }

        foreach (var episodeId in invalidItemIds)
        {
            LogDeletingStaleCache(_logger, episodeId);
        }

        if (invalidItemIds.Count > 0)
        {
            try
            {
                using var deleteDb = Plugin.CreateCacheDbContext();
                await deleteDb.DetectionCache
                    .Where(e => invalidItemIds.Contains(e.ItemId))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or DbException)
            {
                LogDetectionCacheDeleteError(_logger, ex, "stale-detection-cache-delete");
            }
        }

        foreach (var filePath in staleLegacyFiles)
        {
            try
            {
                File.Delete(filePath);
            }
            catch (IOException ex)
            {
                LogDeleteLegacyCacheFileFailed(_logger, ex, filePath);
            }
        }

        // Remove the legacy cache directory if it's now empty.
        if (!string.IsNullOrEmpty(cacheDir))
        {
            TryRemoveEmptyCacheDirectory(cacheDir);
        }
    }

    /// <inheritdoc />
    public async Task MigrateLegacyCachesAsync(IEnumerable<QueuedEpisode> episodes, CancellationToken cancellationToken = default)
    {
        if (_legacyMigrationCompleted)
        {
            return;
        }

        if (!_options.CacheFingerprints)
        {
            return;
        }

        var cacheDir = _options.FingerprintCachePath;
        if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
        {
            _legacyMigrationCompleted = true;
            return;
        }

        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(cacheDir)];
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            if (ex is not DirectoryNotFoundException)
            {
                LogDetectionCacheReadError(_logger, ex, cacheDir);
            }

            _legacyMigrationCompleted = ex is DirectoryNotFoundException;
            return;
        }

        if (files.Count == 0)
        {
            TryRemoveEmptyCacheDirectory(cacheDir);
            _legacyMigrationCompleted = true;
            return;
        }

        var episodeLookup = new Dictionary<Guid, QueuedEpisode>();
        foreach (var ep in episodes)
        {
            episodeLookup.TryAdd(ep.EpisodeId, ep);
        }

        int migrated = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filename = Path.GetFileName(filePath);
            var legacyFile = LegacyDetectionCacheFileName.TryParse(filename);
            if (legacyFile is null || legacyFile.Kind == LegacyCacheKind.Unsupported)
            {
                continue;
            }

            try
            {
                if (await TryMigrateLegacyFileAsync(filePath, filename, legacyFile, episodeLookup, cancellationToken).ConfigureAwait(false))
                {
                    migrated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDetectionCacheReadError(_logger, ex, filePath);
            }
        }

        if (migrated > 0)
        {
            LogBatchMigrationCompleted(_logger, migrated);
        }

        TryRemoveEmptyCacheDirectory(cacheDir);
        _legacyMigrationCompleted = !HasRemainingLegacyMigrationCandidates(cacheDir);
    }

    /// <inheritdoc />
    public async Task<bool> HasCachedFingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        if (!_options.CacheFingerprints)
        {
            return false;
        }

        var (start, end) = episode.GetFingerprintRange(mode);

        try
        {
            using var db = Plugin.CreateCacheDbContext();
            return await WhereCacheKey(
                    db.DetectionCache,
                    episode.EpisodeId,
                    mode,
                    CacheEntryType.Chromaprint,
                    start,
                    end,
                    DetectionCacheVariant.Chromaprint())
                .AnyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            LogDetectionCacheReadError(_logger, ex, $"{episode.EpisodeId:N}-{mode}-{CacheEntryType.Chromaprint}");
            return false;
        }
    }

    /// <summary>
    /// Serializes and compresses a value using Brotli compression.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize and compress.</param>
    /// <returns>The Brotli-compressed data.</returns>
    internal byte[] CompressBrotli<T>(T value)
    {
        var level = _options.CacheCompressionLevel;
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, level))
        {
            JsonSerializer.Serialize(brotli, value);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompresses and deserializes Brotli-compressed data.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="compressed">The Brotli-compressed data.</param>
    /// <returns>The deserialized value, or the default if deserialization returns null.</returns>
    internal T? DecompressBrotli<T>(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(brotli);
    }

    private async Task<bool> WriteJsonCacheForAllModesAsync<T>(
        string legacyCacheKey,
        Guid itemId,
        CacheEntryType type,
        double start,
        double end,
        string variant,
        T[] items,
        CancellationToken cancellationToken)
    {
        var data = CompressBrotli(items);
        var cacheKey = $"{itemId:N}-all-modes-{type}";

        try
        {
            using var db = Plugin.CreateCacheDbContext();
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var existingEntries = await WhereCacheRange(db.DetectionCache, itemId, type, start, end, variant)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var mode in Enum.GetValues<AnalysisMode>())
                {
                    var existing = existingEntries.FirstOrDefault(e => e.Mode == mode);
                    if (existing is not null)
                    {
                        existing.Data = data;
                    }
                    else
                    {
                        db.DetectionCache.Add(new DbDetectionCache(itemId, mode, type, variant, data, start, end));
                    }
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogDetectionCacheWriteError(_logger, ex, cacheKey);

            return false;
        }
    }

    private static string GetLegacyFingerprintCacheKey(Guid itemId, AnalysisMode mode)
    {
        var suffix = mode == AnalysisMode.Credits ? "-credits" : string.Empty;
        return itemId.ToString("N") + suffix;
    }

    private static string GetCacheLogKey(DetectionCacheKey key)
        => $"{key.ItemId:N}-{key.Mode}-{key.Type}-{key.Variant}";

    private static uint[] ParseFingerprintRaw(string raw)
    {
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<uint>(lines.Length);
        foreach (var line in lines)
        {
            if (!uint.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                // Any invalid entry means the file is corrupt, abort so FFmpeg re-analyzes.
                return [];
            }

            result.Add(value);
        }

        return [.. result];
    }

    private static IQueryable<DbDetectionCache> WhereCacheKey(
        IQueryable<DbDetectionCache> query,
        DetectionCacheKey key)
        => WhereCacheKey(query, key.ItemId, key.Mode, key.Type, key.Start, key.End, key.Variant);

    private static IQueryable<DbDetectionCache> WhereCacheKey(
        IQueryable<DbDetectionCache> query,
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        string variant)
        => WhereCacheRange(query, itemId, type, start, end, variant)
            .Where(e => e.Mode == mode);

    private static IQueryable<DbDetectionCache> WhereCacheRange(
        IQueryable<DbDetectionCache> query,
        Guid itemId,
        CacheEntryType type,
        double start,
        double end,
        string variant)
        => query.Where(e => e.ItemId == itemId &&
            e.Type == type &&
            e.Variant == variant &&
            e.Start >= start - CacheTimeTolerance &&
            e.Start <= start + CacheTimeTolerance &&
            e.End >= end - CacheTimeTolerance &&
            e.End <= end + CacheTimeTolerance);

    private static async Task UpsertJsonCacheEntryAsync(
        DetectionCacheDbContext db,
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        string variant,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var existing = await WhereCacheKey(db.DetectionCache, itemId, mode, type, start, end, variant)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Data = data;
        }
        else
        {
            db.DetectionCache.Add(new DbDetectionCache(itemId, mode, type, variant, data, start, end));
        }
    }

    private void DeleteLegacyCacheFilePath(string legacyTextPath)
    {
        try
        {
            File.Delete(legacyTextPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteLegacyCacheFileFailed(_logger, ex, legacyTextPath);
        }
    }

    private bool TryLoadLegacyCache<T>(
        string legacyCacheKey,
        string legacyTextPath,
        DetectionCacheKey key,
        Func<string, T[]> rawParser,
        out T[] result)
    {
        result = [];

        try
        {
            var raw = File.ReadAllText(legacyTextPath, Encoding.UTF8);
            result = rawParser(raw);

            // An empty chromaprint legacy file is corrupt; empty detection result caches are valid.
            if (key.Type == CacheEntryType.Chromaprint && result.Length == 0)
            {
                // Unrecoverable: corrupt/empty fingerprint file. Delete so it is not retried forever.
                DeleteLegacyCacheFilePath(legacyTextPath);
                return false;
            }

            // Crosscheck chromaprint fingerprint duration against current settings.
            if (key.Type == CacheEntryType.Chromaprint)
            {
                var inferredDuration = ChromaprintConstants.InferDuration(result.Length);
                var expectedDuration = Math.Round(key.End - key.Start);

                if (Math.Abs(inferredDuration - expectedDuration) > ChromaprintConstants.DurationTolerance)
                {
                    LogLegacyDurationMismatch(_logger, legacyCacheKey, inferredDuration, expectedDuration);

                    // Unrecoverable: fingerprint was generated with different settings. Delete so it is not retried forever.
                    DeleteLegacyCacheFilePath(legacyTextPath);
                    return false;
                }
            }

            LogMigratingLegacyCache(_logger, legacyCacheKey, GetCacheLogKey(key));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex is not FileNotFoundException)
            {
                LogDetectionCacheReadError(_logger, ex, legacyTextPath);
            }

            return false;
        }
    }

    /// <summary>
    /// Attempts to migrate a single legacy on-disk cache file into the SQLite cache.
    /// </summary>
    private async Task<bool> TryMigrateLegacyFileAsync(
        string filePath,
        string filename,
        LegacyParseResult legacyFile,
        Dictionary<Guid, QueuedEpisode> episodeLookup,
        CancellationToken cancellationToken)
    {
        var itemId = legacyFile.ItemId;
        if (!episodeLookup.TryGetValue(itemId, out var episode))
        {
            return false; // Episode not in queue; stale cache cleanup will handle it.
        }

        if (legacyFile.Kind is LegacyCacheKind.Fingerprint or LegacyCacheKind.CreditFingerprint)
        {
            // Chromaprint fingerprint file
            var mode = legacyFile.Kind == LegacyCacheKind.CreditFingerprint ? AnalysisMode.Credits : AnalysisMode.Introduction;

            var (start, end) = episode.GetFingerprintRange(mode);

            if (end <= start)
            {
                DeleteLegacyCacheFilePath(filePath);
                return false;
            }

            var key = new DetectionCacheKey(itemId, mode, CacheEntryType.Chromaprint, start, end, DetectionCacheVariant.Chromaprint());
            var legacyCacheKey = GetLegacyFingerprintCacheKey(itemId, mode);

            if (await TryReadJsonCacheAsync<uint>(key, cancellationToken).ConfigureAwait(false) is not null)
            {
                DeleteLegacyCacheFilePath(filePath);
                return false;
            }

            if (TryLoadLegacyCache(legacyCacheKey, filePath, key, ParseFingerprintRaw, out var result) &&
                await WriteJsonCacheAsync(key, result, cancellationToken).ConfigureAwait(false))
            {
                DeleteLegacyCacheFilePath(filePath);
                return true;
            }

            return false;
        }

        return legacyFile.Kind switch
        {
            LegacyCacheKind.Silence => await MigrateLegacyTypedAsync(
                filePath,
                filename,
                itemId,
                CacheEntryType.Silence,
                legacyFile.Start,
                legacyFile.End,
                raw => FFmpegOutputParser.ParseSilenceRaw(raw, legacyFile.Start),
                AnalysisMode.Introduction,
                DetectionCacheVariant.Silence(_options.SilenceDetectionMaximumNoise),
                modeAgnostic: true,
                cancellationToken).ConfigureAwait(false),

            // Legacy blackframe caches are Credits-only because blackframe analyzers run in Credits mode.
            LegacyCacheKind.BlackFrameRange or LegacyCacheKind.BlackFrameCredits => await MigrateLegacyTypedAsync(
                filePath,
                filename,
                itemId,
                CacheEntryType.BlackFrame,
                legacyFile.Start,
                legacyFile.End,
                raw => FFmpegOutputParser.OffsetBlackFrames(FFmpegOutputParser.ParseBlackFrames(raw), legacyFile.Start),
                AnalysisMode.Credits,
                legacyFile.Kind == LegacyCacheKind.BlackFrameCredits
                    ? DetectionCacheVariant.BlackFrameCredits(_options.BlackFrameThreshold)
                    : DetectionCacheVariant.BlackFrameRange(_options.BlackFrameThreshold),
                modeAgnostic: false,
                cancellationToken).ConfigureAwait(false),

            LegacyCacheKind.Keyframe => await MigrateLegacyTypedAsync(
                filePath,
                filename,
                itemId,
                CacheEntryType.Keyframe,
                legacyFile.Start,
                legacyFile.End,
                raw => FFmpegOutputParser.ParseKeyFramesRaw(raw, legacyFile.Start),
                AnalysisMode.Introduction,
                DetectionCacheVariant.Keyframe(),
                modeAgnostic: true,
                cancellationToken).ConfigureAwait(false),

            _ => false,
        };
    }

    /// <summary>
    /// Strongly-typed helper that migrates a single non-fingerprint legacy cache file into SQLite.
    /// </summary>
    private async Task<bool> MigrateLegacyTypedAsync<T>(
        string filePath,
        string legacyCacheKey,
        Guid itemId,
        CacheEntryType type,
        double start,
        double end,
        Func<string, T[]> rawParser,
        AnalysisMode mode,
        string variant,
        bool modeAgnostic,
        CancellationToken cancellationToken)
    {
        var key = new DetectionCacheKey(itemId, mode, type, start, end, variant);

        if (!TryLoadLegacyCache(legacyCacheKey, filePath, key, rawParser, out var result))
        {
            return false;
        }

        var written = modeAgnostic
            ? await WriteJsonCacheForAllModesAsync(
                legacyCacheKey, itemId, type, start, end, variant, result, cancellationToken).ConfigureAwait(false)
            : await WriteJsonCacheAsync(key, result, cancellationToken).ConfigureAwait(false);

        if (written)
        {
            DeleteLegacyCacheFilePath(filePath);
        }

        return written;
    }

    private static bool HasRemainingLegacyMigrationCandidates(string cacheDir)
    {
        if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
        {
            return false;
        }

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(cacheDir))
            {
                var legacyFile = LegacyDetectionCacheFileName.TryParse(Path.GetFileName(filePath));
                if (legacyFile is not null && legacyFile.Kind != LegacyCacheKind.Unsupported)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Removes the legacy cache directory if it exists and is empty.
    /// </summary>
    private void TryRemoveEmptyCacheDirectory(string cacheDir)
    {
        try
        {
            if (Directory.Exists(cacheDir) && !Directory.EnumerateFileSystemEntries(cacheDir).Any())
            {
                Directory.Delete(cacheDir);
                LogRemovedEmptyCacheDirectory(_logger, cacheDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: directory removal is not critical.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "DeleteEpisodeCache {FilePath}")]
    private static partial void LogDeleteEpisodeCache(ILogger logger, string filePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete legacy cache file '{FilePath}'")]
    private static partial void LogDeleteLegacyCacheFileFailed(ILogger logger, Exception ex, string filePath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting cache files for episode ID: {EpisodeId}")]
    private static partial void LogDeletingStaleCache(ILogger logger, Guid episodeId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Detection cache hit for {CacheKey}")]
    private static partial void LogDetectionCacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading detection cache from {Path}")]
    private static partial void LogDetectionCacheReadError(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error writing detection cache for {CacheKey}")]
    private static partial void LogDetectionCacheWriteError(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error deleting detection cache for {CacheKey}")]
    private static partial void LogDetectionCacheDeleteError(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Migrating legacy cache {LegacyKey} to {NewKey}")]
    private static partial void LogMigratingLegacyCache(ILogger logger, string legacyKey, string newKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Legacy fingerprint {CacheKey} duration mismatch (inferred {Inferred}s vs expected {Expected}s), re-fingerprinting")]
    private static partial void LogLegacyDurationMismatch(ILogger logger, string cacheKey, double inferred, double expected);

    [LoggerMessage(Level = LogLevel.Information, Message = "Batch legacy cache migration completed: {Count} files migrated to SQLite")]
    private static partial void LogBatchMigrationCompleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Removed empty legacy cache directory: {Path}")]
    private static partial void LogRemovedEmptyCacheDirectory(ILogger logger, string path);
}
