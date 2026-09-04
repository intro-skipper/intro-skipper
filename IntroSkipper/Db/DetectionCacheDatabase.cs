// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Linq.Expressions;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Default implementation of <see cref="IDetectionCacheDatabase"/>. Stateless apart from
/// the retryable schema gate: every operation creates a fresh
/// <see cref="DetectionCacheDbContext"/> from the injected factory.
/// </summary>
internal sealed partial class DetectionCacheDatabase : IDetectionCacheDatabase
{
    private readonly IDbContextFactory<DetectionCacheDbContext> _contextFactory;
    private readonly ILogger _logger;
    private readonly RetryableInitializationGate _initialization;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheDatabase"/> class.
    /// </summary>
    /// <param name="contextFactory">Factory used to create cache database contexts.</param>
    /// <param name="logger">Logger.</param>
    public DetectionCacheDatabase(IDbContextFactory<DetectionCacheDbContext> contextFactory, ILogger<DetectionCacheDatabase> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;

        // The schema work runs inline in the attempt: TryInitialize is synchronous, so
        // the attempt's task is always already complete by the time it is awaited.
        _initialization = new RetryableInitializationGate(() =>
        {
            InitializeCore();
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc/>
    public bool TryInitialize()
    {
        try
        {
            _initialization.AwaitValueAsync(ex => LogCacheDbInitializationError(_logger, ex)).GetAwaiter().GetResult();
            return true;
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
        return db.DetectionCache
            .AsNoTracking()
            .FirstOrDefault(e => e.ItemId == itemId && e.Mode == mode && e.Type == type && e.Start == start && e.End == end);
    }

    /// <inheritdoc/>
    public void Upsert(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, byte[] data, string configHash)
    {
        if (!TryInitialize())
        {
            return;
        }

        // ON CONFLICT targets the unique (ItemId, Mode, Type, Start, End) index, so the
        // existing BLOB is never read back just to be replaced.
        using var db = _contextFactory.CreateDbContext();
        db.Database.ExecuteSql(
            $"""
            INSERT INTO "DetectionCache" ("ItemId", "Mode", "Type", "Start", "End", "Data", "ConfigHash")
            VALUES ({itemId}, {(int)mode}, {(int)type}, {start}, {end}, {data}, {configHash})
            ON CONFLICT("ItemId", "Mode", "Type", "Start", "End") DO UPDATE SET
                "Data" = excluded."Data",
                "ConfigHash" = excluded."ConfigHash"
            """);
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
    public Task<int> DeleteByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
        => DeleteWhereAsync(e => e.Mode == mode, cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<Guid>> GetStaleItemIdsAsync(IReadOnlySet<Guid> validItemIds, CancellationToken cancellationToken = default)
    {
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
    public async Task<int> DeleteForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        // EF.Parameter binds the ID set as a single JSON parameter (json_each), so the
        // delete is one statement regardless of the item count.
        return await DeleteWhereAsync(e => EF.Parameter(ids).Contains(e.ItemId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteEntriesWithUnknownConfigHashAsync(IReadOnlyCollection<string> acceptedConfigHashes, string acceptedHashPrefix, CancellationToken cancellationToken = default)
    {
        var hashes = acceptedConfigHashes.Distinct().ToArray();

        // EF.Parameter binds the accepted set as a single JSON parameter (json_each), so
        // the delete is one statement regardless of how many hashes are accepted.
        return await DeleteWhereAsync(
            e => e.ConfigHash != string.Empty
                && !e.ConfigHash.StartsWith(acceptedHashPrefix)
                && !EF.Parameter(hashes).Contains(e.ConfigHash),
            cancellationToken).ConfigureAwait(false);
    }

    // The cache is an optimization: deletes are best-effort, logging and reporting 0
    // instead of surfacing database failures. DeleteForItem is the synchronous twin.
    private async Task<int> DeleteWhereAsync(Expression<Func<DbDetectionCache, bool>> predicate, CancellationToken cancellationToken)
    {
        if (!TryInitialize())
        {
            return 0;
        }

        try
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.DetectionCache
                .Where(predicate)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            LogCacheDeleteFailed(_logger, ex);
            return 0;
        }
    }

    private void InitializeCore()
    {
        using var db = _contextFactory.CreateDbContext();

        db.EnsureSchema();
        SqlitePragmas.EnforceWal(db.Database);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Detection cache database initialization failed; the next database operation will retry")]
    private static partial void LogCacheDbInitializationError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete detection cache rows; the cache is an optimization, continuing")]
    private static partial void LogCacheDeleteFailed(ILogger logger, Exception exception);
}
