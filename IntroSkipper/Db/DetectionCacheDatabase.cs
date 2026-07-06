// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Default implementation of <see cref="IDetectionCacheDatabase"/>. Stateless apart from
/// the one-shot schema gate: every operation creates a fresh
/// <see cref="DetectionCacheDbContext"/> from the injected factory.
/// </summary>
public sealed partial class DetectionCacheDatabase : IDetectionCacheDatabase
{
    private const int SqliteParameterBatchSize = 500;

    private readonly IDbContextFactory<DetectionCacheDbContext> _contextFactory;
    private readonly ILogger _logger;
    private readonly Lazy<bool> _initialization;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDatabase"/> class.
    /// </summary>
    /// <param name="contextFactory">Factory used to create cache database contexts.</param>
    /// <param name="logger">Logger.</param>
    public DetectionCacheDatabase(IDbContextFactory<DetectionCacheDbContext> contextFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
        _initialization = new Lazy<bool>(InitializeCore, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc/>
    public void Initialize() => _ = _initialization.Value;

    /// <inheritdoc/>
    public DbDetectionCache? FindEntry(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end)
    {
        Initialize();
        using var db = _contextFactory.CreateDbContext();
        return db.DetectionCache
            .AsNoTracking()
            .FirstOrDefault(e => e.ItemId == itemId && e.Mode == mode && e.Type == type && e.Start == start && e.End == end);
    }

    /// <inheritdoc/>
    public void Upsert(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, byte[] data, string configHash)
    {
        Initialize();
        using var db = _contextFactory.CreateDbContext();

        // NOTE: Start/End are compared with == which is safe only because the exact same
        // double values that were written are used for lookup (no intermediate arithmetic).
        var existing = db.DetectionCache
            .FirstOrDefault(e => e.ItemId == itemId && e.Mode == mode && e.Type == type && e.Start == start && e.End == end);

        if (existing is not null)
        {
            existing.Data = data;
            existing.ConfigHash = configHash;
        }
        else
        {
            db.DetectionCache.Add(new DbDetectionCache(itemId, mode, type, data, start, end, configHash));
        }

        db.SaveChanges();
    }

    /// <inheritdoc/>
    public bool HasEntry(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, string expectedConfigHash)
    {
        Initialize();
        using var db = _contextFactory.CreateDbContext();
        return db.DetectionCache.Any(e =>
            e.ItemId == itemId &&
            e.Mode == mode &&
            e.Type == type &&
            e.Start == start &&
            e.End == end &&
            (e.ConfigHash == string.Empty || e.ConfigHash == expectedConfigHash));
    }

    /// <inheritdoc/>
    public int DeleteForItem(Guid itemId)
    {
        Initialize();
        using var db = _contextFactory.CreateDbContext();
        return db.DetectionCache.Where(e => e.ItemId == itemId).ExecuteDelete();
    }

    /// <inheritdoc/>
    public int DeleteByMode(AnalysisMode mode)
    {
        Initialize();
        using var db = _contextFactory.CreateDbContext();
        return db.DetectionCache.Where(e => e.Mode == mode).ExecuteDelete();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Guid>> GetStaleItemIdsAsync(IReadOnlySet<Guid> validItemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validItemIds);

        Initialize();
        using var db = _contextFactory.CreateDbContext();
        var cachedItemIds = await db.DetectionCache
            .AsNoTracking()
            .Select(e => e.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return cachedItemIds.Where(id => !validItemIds.Contains(id)).ToHashSet();
    }

    /// <inheritdoc/>
    public async Task<int> DeleteForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        Initialize();
        using var db = _contextFactory.CreateDbContext();

        var deleted = 0;
        foreach (var batch in ids.Chunk(SqliteParameterBatchSize))
        {
            deleted += await db.DetectionCache
                .Where(e => batch.Contains(e.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return deleted;
    }

    private bool InitializeCore()
    {
        try
        {
            using var db = _contextFactory.CreateDbContext();
            db.EnsureSchema();
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            // Matches the plugin's historical constructor behavior: the cache is a
            // performance optimization, so schema failures are logged and swallowed.
            LogCacheDbInitializationError(_logger, ex);
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error initializing detection cache database")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);
}
