// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Db;

/// <summary>
/// Default <see cref="IDetectionCacheStore"/> implementation over <see cref="IDbContextFactory{TContext}"/>.
/// Every operation runs the <see cref="IDatabaseInitializer"/> cache gate (when supplied) before
/// opening a context, guaranteeing the schema exists before any query runs.
/// </summary>
internal sealed class DetectionCacheStore : IDetectionCacheStore
{
    private readonly IDbContextFactory<DetectionCacheDbContext> _contextFactory;
    private readonly IDatabaseInitializer? _initializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectionCacheStore"/> class.
    /// </summary>
    /// <param name="contextFactory">Detection cache database context factory.</param>
    /// <param name="initializer">Optional initialization gate. Pass <see langword="null"/> only when the schema is guaranteed to exist already.</param>
    public DetectionCacheStore(IDbContextFactory<DetectionCacheDbContext> contextFactory, IDatabaseInitializer? initializer = null)
    {
        _contextFactory = contextFactory;
        _initializer = initializer;
    }

    /// <inheritdoc/>
    public DbDetectionCache? Find(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end)
    {
        using var db = CreateContext();
        return db.DetectionCache
            .AsNoTracking()
            .FirstOrDefault(e => e.ItemId == itemId && e.Mode == mode && e.Type == type && e.Start == start && e.End == end);
    }

    /// <inheritdoc/>
    public bool Exists(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, string expectedConfigHash)
    {
        using var db = CreateContext();
        return db.DetectionCache.Any(e =>
            e.ItemId == itemId &&
            e.Mode == mode &&
            e.Type == type &&
            e.Start == start &&
            e.End == end &&
            (e.ConfigHash == string.Empty || e.ConfigHash == expectedConfigHash));
    }

    /// <inheritdoc/>
    public void Upsert(Guid itemId, AnalysisMode mode, CacheEntryType type, double start, double end, byte[] data, string configHash)
    {
        using var db = CreateContext();
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
    public void DeleteForItem(Guid itemId)
    {
        using var db = CreateContext();
        db.DetectionCache.Where(e => e.ItemId == itemId).ExecuteDelete();
    }

    /// <inheritdoc/>
    public void DeleteByMode(AnalysisMode mode)
    {
        using var db = CreateContext();
        db.DetectionCache.Where(e => e.Mode == mode).ExecuteDelete();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Guid>> GetItemIdsAsync(CancellationToken cancellationToken = default)
    {
        using var db = CreateContext();
        return await db.DetectionCache
            .AsNoTracking()
            .Select(e => e.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteForItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return 0;
        }

        var ids = itemIds as Guid[] ?? [.. itemIds];

        using var db = CreateContext();
        return await db.DetectionCache
            .Where(e => EF.Parameter(ids).Contains(e.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private DetectionCacheDbContext CreateContext()
    {
        _initializer?.EnsureCacheDbReady();
        return _contextFactory.CreateDbContext();
    }
}
