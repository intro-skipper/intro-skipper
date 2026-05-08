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

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Provides detection-cache operations backed by a SQLite database and legacy on-disk text files.
/// </summary>
public sealed partial class DetectionCacheService : IDetectionCacheService
{
    private const double CacheTimeTolerance = 1e-6;

    private readonly IFFmpegOptionsProvider _options;
    private readonly ILogger<DetectionCacheService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheService"/> class.
    /// </summary>
    /// <param name="options">Options provider for cache configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public DetectionCacheService(
        IFFmpegOptionsProvider options,
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

            var entry = await db.DetectionCache
                .FirstOrDefaultAsync(
                    e => e.ItemId == key.ItemId &&
                        e.Mode == key.Mode &&
                        e.Type == key.Type &&
                        e.Start >= key.Start - CacheTimeTolerance &&
                        e.Start <= key.Start + CacheTimeTolerance &&
                        e.End >= key.End - CacheTimeTolerance &&
                        e.End <= key.End + CacheTimeTolerance,
                    cancellationToken)
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

            await UpsertJsonCacheEntryAsync(db, key.ItemId, key.Mode, key.Type, key.Start, key.End, data, cancellationToken)
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
    public async Task<T[]?> TryReadOrMigrateCacheAsync<T>(DetectionCacheKey key, DetectionCacheKind kind, Func<string, T[]> rawParser, CancellationToken cancellationToken = default)
    {
        var result = await TryReadJsonCacheAsync<T>(key, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            return result;
        }

        var legacyCacheKey = GetLegacyDetectionCacheKey(key, kind);
        var legacyPath = GetLegacyFilePath(legacyCacheKey);
        return kind is DetectionCacheKind.Silence or DetectionCacheKind.Keyframe
            ? await LoadLegacyCacheForAllModesAsync(legacyCacheKey, legacyPath, key, rawParser, cancellationToken).ConfigureAwait(false)
            : await LoadLegacyCacheAsync(legacyCacheKey, legacyPath, key, rawParser, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<uint[]?> LoadCachedFingerprintAsync(QueuedEpisode episode, AnalysisMode mode, double start, double end, CancellationToken cancellationToken = default)
    {
        if (!_options.CacheFingerprints)
        {
            return null;
        }

        var key = new DetectionCacheKey(episode.EpisodeId, mode, CacheEntryType.Chromaprint, start, end);
        var result = await TryReadJsonCacheAsync<uint>(key, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            return result;
        }

        var legacyCacheKey = GetLegacyFingerprintCacheKey(episode.EpisodeId, mode);
        return await LoadLegacyCacheAsync(
                legacyCacheKey,
                GetLegacyFilePath(legacyCacheKey),
                key,
                ParseFingerprintRaw,
                cancellationToken)
            .ConfigureAwait(false);
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

        var cacheDir = _options.FingerprintCachePath;
        if (cacheDir is not null && Directory.Exists(cacheDir))
        {
            foreach (var filePath in Directory.EnumerateFiles(cacheDir)
                .Where(f => mode == AnalysisMode.Introduction
                    ? !Path.GetFileName(f).Contains("credit", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(f).Contains("blackframes", StringComparison.OrdinalIgnoreCase)
                    : Path.GetFileName(f).Contains("credit", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(f).Contains("blackframes", StringComparison.OrdinalIgnoreCase)))
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

        var invalidItemIds = new HashSet<Guid>();
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
                if (!TryGetLegacyDetectionCacheParts(filename, out var legacyId, out _) ||
                    enabledItemIds.Contains(legacyId))
                {
                    continue;
                }

                invalidItemIds.Add(legacyId);
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
            if (await db.DetectionCache.AnyAsync(
                e => e.ItemId == episode.EpisodeId &&
                    e.Mode == mode &&
                    e.Type == CacheEntryType.Chromaprint &&
                    e.Start >= start - CacheTimeTolerance &&
                    e.Start <= start + CacheTimeTolerance &&
                    e.End >= end - CacheTimeTolerance &&
                    e.End <= end + CacheTimeTolerance,
                cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (DbException ex)
        {
            LogDetectionCacheReadError(_logger, ex, $"{episode.EpisodeId:N}-{mode}-{CacheEntryType.Chromaprint}");
        }

        var legacyPath = GetLegacyFilePath(GetLegacyFingerprintCacheKey(episode.EpisodeId, mode));

        return File.Exists(legacyPath);
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
        T[] items,
        CancellationToken cancellationToken)
    {
        if (!_options.CacheFingerprints)
        {
            return false;
        }

        var data = CompressBrotli(items);
        var cacheKey = $"{itemId:N}-all-modes-{type}";

        try
        {
            using var db = Plugin.CreateCacheDbContext();
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                LogMigratingLegacyCache(_logger, legacyCacheKey, cacheKey);

                var existingEntries = await db.DetectionCache
                    .Where(e => e.ItemId == itemId &&
                        e.Type == type &&
                        e.Start >= start - CacheTimeTolerance &&
                        e.Start <= start + CacheTimeTolerance &&
                        e.End >= end - CacheTimeTolerance &&
                        e.End <= end + CacheTimeTolerance)
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
                        db.DetectionCache.Add(new DbDetectionCache(itemId, mode, type, data, start, end));
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

    private static string GetLegacyDetectionCacheKey(DetectionCacheKey key, DetectionCacheKind kind)
        => kind switch
        {
            DetectionCacheKind.Silence => string.Format(
                CultureInfo.InvariantCulture,
                "{0}-silence-{1}-{2}-v2",
                key.ItemId.ToString("N"),
                key.Start,
                key.End),
            DetectionCacheKind.BlackFrameRange => string.Format(
                CultureInfo.InvariantCulture,
                "{0}-blackframes-{1}-{2}-v1",
                key.ItemId.ToString("N"),
                key.Start,
                key.End),
            DetectionCacheKind.BlackFrameAlt => string.Format(
                CultureInfo.InvariantCulture,
                "{0}-blackframes-{1}-alt",
                key.ItemId.ToString("N"),
                key.Start),
            DetectionCacheKind.Keyframe => string.Format(
                CultureInfo.InvariantCulture,
                "{0}-keyframes-{1}-{2}-v1",
                key.ItemId.ToString("N"),
                key.Start,
                key.End),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static string GetCacheLogKey(DetectionCacheKey key)
        => $"{key.ItemId:N}-{key.Mode}-{key.Type}";

    private static uint[] ParseFingerprintRaw(string raw)
    {
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<uint>(lines.Length);
        foreach (var line in lines)
        {
            if (!uint.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                // Any invalid entry means the file is corrupt — abort so FFmpeg re-analyzes.
                return [];
            }

            result.Add(value);
        }

        return [.. result];
    }

    private static async Task UpsertJsonCacheEntryAsync(
        DetectionCacheDbContext db,
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var existing = await db.DetectionCache
            .FirstOrDefaultAsync(
                e => e.ItemId == itemId &&
                    e.Mode == mode &&
                    e.Type == type &&
                    e.Start >= start - CacheTimeTolerance &&
                    e.Start <= start + CacheTimeTolerance &&
                    e.End >= end - CacheTimeTolerance &&
                    e.End <= end + CacheTimeTolerance,
                cancellationToken)
            .ConfigureAwait(false);

        UpsertJsonCacheEntry(db, existing, itemId, mode, type, start, end, data);
    }

    private static void UpsertJsonCacheEntry(
        DetectionCacheDbContext db,
        DbDetectionCache? existing,
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end,
        byte[] data)
    {
        if (existing is not null)
        {
            existing.Data = data;
        }
        else
        {
            db.DetectionCache.Add(new DbDetectionCache(itemId, mode, type, data, start, end));
        }
    }

    internal static bool TryGetLegacyDetectionCacheParts(string filename, out Guid itemId, out string suffix)
    {
        itemId = Guid.Empty;
        suffix = string.Empty;
        if (filename.Length < 32 || (filename.Length > 32 && filename[32] != '-'))
        {
            return false;
        }

        var itemIdText = filename[..32];
        if (!Guid.TryParseExact(itemIdText, "N", out itemId) ||
            !string.Equals(itemIdText, itemId.ToString("N"), StringComparison.Ordinal))
        {
            return false;
        }

        suffix = filename.Length == 32 ? string.Empty : filename[33..];
        return true;
    }

    private string GetLegacyFilePath(string cacheKey)
        => Path.Join(_options.FingerprintCachePath ?? string.Empty, cacheKey);

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

    private async Task<T[]?> LoadLegacyCacheAsync<T>(
        string legacyCacheKey,
        string legacyTextPath,
        DetectionCacheKey key,
        Func<string, T[]> rawParser,
        CancellationToken cancellationToken)
    {
        if (!TryLoadLegacyCache(legacyCacheKey, legacyTextPath, key, rawParser, out var result))
        {
            return null;
        }

        if (await WriteJsonCacheAsync(key, result, cancellationToken).ConfigureAwait(false))
        {
            DeleteLegacyCacheFilePath(legacyTextPath);
        }

        return result;
    }

    private async Task<T[]?> LoadLegacyCacheForAllModesAsync<T>(
        string legacyCacheKey,
        string legacyTextPath,
        DetectionCacheKey key,
        Func<string, T[]> rawParser,
        CancellationToken cancellationToken)
    {
        if (!TryLoadLegacyCache(legacyCacheKey, legacyTextPath, key, rawParser, out var result))
        {
            return null;
        }

        if (await WriteJsonCacheForAllModesAsync(
                legacyCacheKey,
                key.ItemId,
                key.Type,
                key.Start,
                key.End,
                result,
                cancellationToken).ConfigureAwait(false))
        {
            DeleteLegacyCacheFilePath(legacyTextPath);
        }

        return result;
    }

    private bool TryLoadLegacyCache<T>(
        string legacyCacheKey,
        string legacyTextPath,
        DetectionCacheKey key,
        Func<string, T[]> rawParser,
        out T[] result)
    {
        result = [];

        if (!_options.CacheFingerprints)
        {
            return false;
        }

        // Migrate legacy on-disk text files into the SQLite cache.
        if (!File.Exists(legacyTextPath))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(legacyTextPath, Encoding.UTF8);
            result = rawParser(raw);

            // An empty chromaprint legacy file is corrupt; empty detection result caches are valid.
            if (key.Type == CacheEntryType.Chromaprint && result.Length == 0)
            {
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

                    DeleteLegacyCacheFilePath(legacyTextPath);
                    return false;
                }
            }

            LogMigratingLegacyCache(_logger, legacyCacheKey, GetCacheLogKey(key));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // FileNotFoundException is a subclass of IOException and is the normal case when the
            // legacy text file simply does not exist — suppress it silently to avoid log noise.
            if (ex is not FileNotFoundException)
            {
                LogDetectionCacheReadError(_logger, ex, legacyTextPath);
            }

            return false;
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
}
