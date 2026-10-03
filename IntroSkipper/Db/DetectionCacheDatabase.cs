// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Data.Common;
using System.Linq.Expressions;
using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Db;

/// <summary>
/// Cohesive facade over the detection cache database (<c>introskipper-cache.db</c>).
/// Owns every read and write against <see cref="DetectionCacheDbContext"/> as well as the
/// schema lifecycle (<c>EnsureCreated</c> with delete-and-recreate corruption recovery).
/// The synchronous members mirror the synchronous call patterns of the analysis pipeline.
/// Initialization failures make the cache temporarily unavailable; operations return neutral
/// results and retry initialization on the next call. Deletes are best-effort: the cache is
/// an optimization, so database errors are logged and swallowed (returning 0) while
/// cancellation still propagates. Stateless apart from the retryable schema gate: every
/// operation creates a fresh <see cref="DetectionCacheDbContext"/> from the injected
/// factory.
/// </summary>
public sealed partial class DetectionCacheDatabase
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

    /// <summary>
    /// Ensures the cache schema exists, recreating corrupt cache files when possible.
    /// Concurrent callers share one attempt; a failed attempt is logged and reset so the
    /// next call retries.
    /// </summary>
    /// <returns><see langword="true"/> when the cache is ready.</returns>
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

    /// <summary>
    /// Returns the cache entry matching the exact key, or <see langword="null"/>.
    /// Start/End are compared with equality, which is safe only because the exact same
    /// double values that were written are used for lookup.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Start of the analyzed range.</param>
    /// <param name="end">End of the analyzed range.</param>
    /// <returns>The matching entry, or <see langword="null"/>.</returns>
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

    /// <summary>
    /// Inserts or updates the cache entry for the given key in one statement.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="start">Start of the analyzed range.</param>
    /// <param name="end">End of the analyzed range.</param>
    /// <param name="data">Compressed detection data.</param>
    /// <param name="configHash">Configuration hash that produced the data.</param>
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

    /// <summary>
    /// Deletes all cache entries for an item. Synchronous for the library-removed
    /// event handler, which is not async.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
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

    /// <summary>
    /// Deletes all cache entries for an analysis mode.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
    public Task<int> DeleteByModeAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
        => DeleteWhereAsync(e => e.Mode == mode, cancellationToken);

    /// <summary>
    /// Returns the distinct item IDs present in the cache that are not part of
    /// <paramref name="validItemIds"/>. The valid set is bound as a single JSON
    /// parameter (<c>json_each</c>), so the query is safe for arbitrarily large libraries.
    /// </summary>
    /// <param name="validItemIds">Item IDs that are still valid.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stale item IDs.</returns>
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

    /// <summary>
    /// Deletes all cache entries for the given items in a single statement; the ID set
    /// is bound as one JSON parameter, so the item count is unbounded.
    /// </summary>
    /// <param name="itemIds">Item IDs whose cache entries should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
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

    /// <summary>
    /// Deletes every cache entry whose configuration hash is non-empty, does not start
    /// with <paramref name="acceptedHashPrefix"/>, and is not in
    /// <paramref name="acceptedConfigHashes"/>. The accepted set is bound as a single
    /// JSON parameter (<c>json_each</c>), so its size is unbounded.
    /// </summary>
    /// <param name="acceptedConfigHashes">Configuration hashes whose entries are kept.</param>
    /// <param name="acceptedHashPrefix">Hash prefix whose entries are kept.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of deleted rows; 0 when the delete failed.</returns>
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
