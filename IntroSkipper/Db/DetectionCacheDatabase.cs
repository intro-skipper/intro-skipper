// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

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
    public DetectionCacheDatabase(IDbContextFactory<DetectionCacheDbContext> contextFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _contextFactory = contextFactory;
        _logger = logger;
        _initialization = new RetryableInitializationGate<bool>(InitializeCore);
    }

    /// <inheritdoc/>
    public void Initialize()
    {
        var initialization = _initialization.GetAttempt();

        try
        {
            _ = initialization.Value;
        }
        catch (Exception ex)
        {
            if (_initialization.ResetIfCurrent(initialization))
            {
                LogCacheDbInitializationError(_logger, ex);
            }

            throw;
        }
    }

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

        var validIds = validItemIds.ToArray();

        Initialize();
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

        // EF.Parameter binds the ID set as a single JSON parameter (json_each), so the
        // delete is one statement regardless of the item count.
        return await db.DetectionCache
            .Where(e => EF.Parameter(ids).Contains(e.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private bool InitializeCore()
    {
        using var db = _contextFactory.CreateDbContext();

        db.EnsureSchema();

        // WAL is a persistent database property, but EF only sets it when *it*
        // creates the database file. Enforce it idempotently so databases
        // vacuumed or recreated by external tooling are covered as well.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Detection cache database initialization failed; the next database operation will retry")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);
}
