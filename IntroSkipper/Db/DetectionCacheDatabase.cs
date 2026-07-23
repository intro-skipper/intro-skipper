// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Default implementation of <see cref="IDetectionCacheDatabase"/>. Stateless apart from
/// the retryable schema gate: every operation creates a fresh
/// <see cref="DetectionCacheDbContext"/> from the injected factory.
/// </summary>
public sealed partial class DetectionCacheDatabase : IDetectionCacheDatabase
{
    private readonly IDbContextFactory<DetectionCacheDbContext> _contextFactory;
    private readonly ILogger _logger;
    private readonly RetryableInitializationGate<bool> _initialization;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDatabase"/> class.
    /// </summary>
    /// <param name="contextFactory">Factory used to create cache database contexts.</param>
    /// <param name="logger">Logger.</param>
    public DetectionCacheDatabase(IDbContextFactory<DetectionCacheDbContext> contextFactory, ILogger<DetectionCacheDatabase> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
        _initialization = new RetryableInitializationGate<bool>(InitializeCore);
    }

    /// <inheritdoc/>
    public bool TryInitialize()
    {
        try
        {
            return _initialization.GetValue(ex => LogCacheDbInitializationError(_logger, ex));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public DbDetectionCache? FindEntry(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end)
    {
        if (!TryInitialize())
        {
            return null;
        }

        using var db = _contextFactory.CreateDbContext();
        return QueryByKey(db, itemId, mode, type, start, end)
            .AsNoTracking()
            .FirstOrDefault();
    }

    /// <inheritdoc/>
    public void Upsert(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, byte[] data, string configHash)
    {
        if (!TryInitialize())
        {
            return;
        }

        using var db = _contextFactory.CreateDbContext();

        var existing = QueryByKey(db, itemId, mode, type, start, end).FirstOrDefault();

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
        if (!TryInitialize())
        {
            return false;
        }

        using var db = _contextFactory.CreateDbContext();
        return QueryByKey(db, itemId, mode, type, start, end)
            .Any(e => e.ConfigHash == string.Empty || e.ConfigHash == expectedConfigHash);
    }

    /// <inheritdoc/>
    public int DeleteForItem(Guid itemId)
    {
        if (!TryInitialize())
        {
            return 0;
        }

        try
        {
            using var db = _contextFactory.CreateDbContext();
            return db.DetectionCache.Where(e => e.ItemId == itemId).ExecuteDelete();
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogCacheDeleteFailed(_logger, ex);
            return 0;
        }
    }

    /// <inheritdoc/>
    public int DeleteByMode(AnalysisMode mode)
    {
        if (!TryInitialize())
        {
            return 0;
        }

        try
        {
            using var db = _contextFactory.CreateDbContext();
            return db.DetectionCache.Where(e => e.Mode == mode).ExecuteDelete();
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogCacheDeleteFailed(_logger, ex);
            return 0;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Guid>> GetStaleItemIdsAsync(IReadOnlySet<Guid> validItemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validItemIds);

        var validIds = validItemIds.ToArray();

        if (!TryInitialize())
        {
            return [];
        }

        using var db = _contextFactory.CreateDbContext();

        // EF.Parameter binds the valid set as a single JSON parameter (json_each), so
        // the NOT-IN is safe for arbitrarily large libraries.
        return await db.DetectionCache
            .AsNoTracking()
            .Select(e => e.ItemId)
            .Distinct()
            .Where(id => !EF.Parameter(validIds).Contains(id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbDetectionCache>> GetEntriesForItemAsync(Guid itemId, IReadOnlyCollection<CacheEntryType> types, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(types);

        var typeArray = types.Distinct().ToArray();
        if (typeArray.Length == 0 || !TryInitialize())
        {
            return [];
        }

        using var db = _contextFactory.CreateDbContext();

        return await db.DetectionCache
            .AsNoTracking()
            .Where(e => e.ItemId == itemId && typeArray.Contains(e.Type))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DetectionCacheEntryRange>> GetEntryRangesForItemAsync(Guid itemId, IReadOnlyCollection<CacheEntryType> types, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(types);

        var typeArray = types.Distinct().ToArray();
        if (typeArray.Length == 0 || !TryInitialize())
        {
            return [];
        }

        using var db = _contextFactory.CreateDbContext();

        return await db.DetectionCache
            .AsNoTracking()
            .Where(e => e.ItemId == itemId && typeArray.Contains(e.Type))
            .Select(e => new DetectionCacheEntryRange(e.Type, e.Mode, e.Start, e.End, e.ConfigHash))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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

        if (!TryInitialize())
        {
            return 0;
        }

        try
        {
            using var db = _contextFactory.CreateDbContext();

            // EF.Parameter binds the ID set as a single JSON parameter (json_each), so the
            // delete is one statement regardless of the item count.
            return await db.DetectionCache
                .Where(e => EF.Parameter(ids).Contains(e.ItemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogCacheDeleteFailed(_logger, ex);
            return 0;
        }
    }

    /// <summary>
    /// Filters the cache table by the full cache key. Start/End are compared with ==
    /// which is safe only because the exact same double values that were written are
    /// used for lookup (no intermediate arithmetic).
    /// </summary>
    /// <param name="db">The cache database context.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Segment start.</param>
    /// <param name="end">Segment end.</param>
    /// <returns>The entries matching the cache key.</returns>
    private static IQueryable<DbDetectionCache> QueryByKey(DetectionCacheDbContext db, Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end)
        => db.DetectionCache
            .Where(e => e.ItemId == itemId && e.Mode == mode && e.Type == type && e.Start == start && e.End == end);

    private bool InitializeCore()
    {
        using var db = _contextFactory.CreateDbContext();

        db.EnsureSchema();
        SqlitePragmas.EnforceWal(db.Database);

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Detection cache database initialization failed; the next database operation will retry")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete detection cache rows; the cache is an optimization, continuing")]
    private static partial void LogCacheDeleteFailed(ILogger logger, Exception exception);
}
