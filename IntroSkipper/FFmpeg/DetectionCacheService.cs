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
    private volatile bool _legacyMigrationCompleted;

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

    private enum DetectionCacheKind
    {
        Silence,
        BlackFrameRange,
        BlackFrameAlt,
        Keyframe
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
        catch (DirectoryNotFoundException)
        {
            _legacyMigrationCompleted = true;
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
            if (!TryGetLegacyDetectionCacheParts(filename, out var itemId, out var suffix))
            {
                continue;
            }

            try
            {
                if (await TryMigrateLegacyFileAsync(filePath, filename, itemId, suffix, episodeLookup, cancellationToken).ConfigureAwait(false))
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
                    end)
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

                var existingEntries = await WhereCacheRange(db.DetectionCache, itemId, type, start, end)
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

    private static IQueryable<DbDetectionCache> WhereCacheKey(
        IQueryable<DbDetectionCache> query,
        DetectionCacheKey key)
        => WhereCacheKey(query, key.ItemId, key.Mode, key.Type, key.Start, key.End);

    private static IQueryable<DbDetectionCache> WhereCacheKey(
        IQueryable<DbDetectionCache> query,
        Guid itemId,
        AnalysisMode mode,
        CacheEntryType type,
        double start,
        double end)
        => WhereCacheRange(query, itemId, type, start, end)
            .Where(e => e.Mode == mode);

    private static IQueryable<DbDetectionCache> WhereCacheRange(
        IQueryable<DbDetectionCache> query,
        Guid itemId,
        CacheEntryType type,
        double start,
        double end)
        => query.Where(e => e.ItemId == itemId &&
            e.Type == type &&
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
        byte[] data,
        CancellationToken cancellationToken)
    {
        var existing = await WhereCacheKey(db.DetectionCache, itemId, mode, type, start, end)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

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
        if (!Guid.TryParseExact(itemIdText, "N", out itemId))
        {
            return false;
        }

        suffix = filename.Length == 32 ? string.Empty : filename[33..];
        return true;
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

        if (!_options.CacheFingerprints)
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
        Guid itemId,
        string suffix,
        Dictionary<Guid, QueuedEpisode> episodeLookup,
        CancellationToken cancellationToken)
    {
        // Determine what kind of legacy file this is based on its suffix.
        // Fingerprint files: "{guid}" or "{guid}-credits"
        // Silence files: "{guid}-silence-{start}-{end}-v2"
        // BlackFrame files: "{guid}-blackframes-{start}-{end}-v1" or "{guid}-blackframes-{start}-alt"
        // Keyframe files: "{guid}-keyframes-{start}-{end}-v1"

        if (string.IsNullOrEmpty(suffix) || suffix == "credits")
        {
            // Chromaprint fingerprint file
            var mode = suffix == "credits" ? AnalysisMode.Credits : AnalysisMode.Introduction;

            if (!episodeLookup.TryGetValue(itemId, out var episode))
            {
                return false; // Episode not in queue; stale cache cleanup will handle it
            }

            var (start, end) = episode.GetFingerprintRange(mode);
            var key = new DetectionCacheKey(itemId, mode, CacheEntryType.Chromaprint, start, end);
            var legacyCacheKey = GetLegacyFingerprintCacheKey(itemId, mode);

            if (TryLoadLegacyCache(legacyCacheKey, filePath, key, ParseFingerprintRaw, out var result) &&
                await WriteJsonCacheAsync(key, result, cancellationToken).ConfigureAwait(false))
            {
                DeleteLegacyCacheFilePath(filePath);
                return true;
            }

            return false;
        }

        // Non-fingerprint legacy files: parse the suffix to build a DetectionCacheKey
        if (TryParseLegacySuffix(suffix, out var cacheKind, out var legacyStart, out var legacyEnd))
        {
            return cacheKind switch
            {
                DetectionCacheKind.Silence => await MigrateLegacyTypedAsync(
                    filePath,
                    filename,
                    itemId,
                    CacheEntryType.Silence,
                    legacyStart,
                    legacyEnd,
                    raw => FFmpegOutputParser.ParseSilenceRaw(raw, legacyStart),
                    AnalysisMode.Introduction,
                    modeAgnostic: true,
                    cancellationToken).ConfigureAwait(false),

                // Legacy blackframe caches are Credits-only because blackframe analyzers run in Credits mode.
                DetectionCacheKind.BlackFrameRange or DetectionCacheKind.BlackFrameAlt => await MigrateLegacyTypedAsync(
                    filePath,
                    filename,
                    itemId,
                    CacheEntryType.BlackFrame,
                    legacyStart,
                    legacyEnd,
                    static raw => FFmpegOutputParser.ParseBlackFrames(raw),
                    AnalysisMode.Credits,
                    modeAgnostic: false,
                    cancellationToken).ConfigureAwait(false),

                DetectionCacheKind.Keyframe => await MigrateLegacyTypedAsync(
                    filePath,
                    filename,
                    itemId,
                    CacheEntryType.Keyframe,
                    legacyStart,
                    legacyEnd,
                    raw => FFmpegOutputParser.ParseKeyFramesRaw(raw, legacyStart),
                    AnalysisMode.Introduction,
                    modeAgnostic: true,
                    cancellationToken).ConfigureAwait(false),

                _ => false,
            };
        }

        return false;
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
        bool modeAgnostic,
        CancellationToken cancellationToken)
    {
        var key = new DetectionCacheKey(itemId, mode, type, start, end);

        if (!TryLoadLegacyCache(legacyCacheKey, filePath, key, rawParser, out var result))
        {
            return false;
        }

        var written = modeAgnostic
            ? await WriteJsonCacheForAllModesAsync(
                legacyCacheKey, itemId, type, start, end, result, cancellationToken).ConfigureAwait(false)
            : await WriteJsonCacheAsync(key, result, cancellationToken).ConfigureAwait(false);

        if (written)
        {
            DeleteLegacyCacheFilePath(filePath);
        }

        return written;
    }

    /// <summary>
    /// Parses the suffix portion of a legacy cache filename to determine its kind and time range.
    /// </summary>
    private static bool TryParseLegacySuffix(
        string suffix,
        out DetectionCacheKind kind,
        out double start,
        out double end)
    {
        kind = default;
        start = 0;
        end = 0;

        // silence-{start}-{end}-v2
        if (suffix.StartsWith("silence-", StringComparison.Ordinal) && suffix.EndsWith("-v2", StringComparison.Ordinal))
        {
            var inner = suffix["silence-".Length..^"-v2".Length];
            var parts = inner.Split('-');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], CultureInfo.InvariantCulture, out start) &&
                double.TryParse(parts[1], CultureInfo.InvariantCulture, out end))
            {
                kind = DetectionCacheKind.Silence;
                return true;
            }
        }

        // blackframes-{start}-{end}-v1
        else if (suffix.StartsWith("blackframes-", StringComparison.Ordinal) && suffix.EndsWith("-v1", StringComparison.Ordinal))
        {
            var inner = suffix["blackframes-".Length..^"-v1".Length];
            var parts = inner.Split('-');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], CultureInfo.InvariantCulture, out start) &&
                double.TryParse(parts[1], CultureInfo.InvariantCulture, out end))
            {
                kind = DetectionCacheKind.BlackFrameRange;
                return true;
            }
        }

        // blackframes-{start}-alt
        else if (suffix.StartsWith("blackframes-", StringComparison.Ordinal) && suffix.EndsWith("-alt", StringComparison.Ordinal))
        {
            var inner = suffix["blackframes-".Length..^"-alt".Length];
            if (double.TryParse(inner, CultureInfo.InvariantCulture, out start))
            {
                kind = DetectionCacheKind.BlackFrameAlt;
                end = 0;
                return true;
            }
        }

        // keyframes-{start}-{end}-v1
        else if (suffix.StartsWith("keyframes-", StringComparison.Ordinal) && suffix.EndsWith("-v1", StringComparison.Ordinal))
        {
            var inner = suffix["keyframes-".Length..^"-v1".Length];
            var parts = inner.Split('-');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], CultureInfo.InvariantCulture, out start) &&
                double.TryParse(parts[1], CultureInfo.InvariantCulture, out end))
            {
                kind = DetectionCacheKind.Keyframe;
                return true;
            }
        }

        return false;
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
                if (TryGetLegacyDetectionCacheParts(Path.GetFileName(filePath), out _, out var suffix) &&
                    IsSupportedLegacySuffix(suffix))
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

    private static bool IsSupportedLegacySuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix) || suffix == "credits")
        {
            return true;
        }

        return TryParseLegacySuffix(suffix, out _, out _, out _);
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
