// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Repositories;

/// <summary>
/// Repository implementation for segment data access.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SegmentRepository"/> class.
/// </remarks>
/// <param name="dbContext">Database context.</param>
public class SegmentRepository(IntroSkipperDbContext dbContext) : ISegmentRepository
{
    private readonly IntroSkipperDbContext _dbContext = dbContext;

    /// <inheritdoc/>
    public async Task<DbSegment?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DbSegment
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DbSegment
            .Where(s => s.ItemId == itemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetBySeasonIdAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DbSegment
            .Where(s => s.SeasonId == seasonId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetByItemIdAndTypeAsync(Guid itemId, AnalysisMode type, CancellationToken cancellationToken = default)
    {
        return await _dbContext.DbSegment
            .Where(s => s.ItemId == itemId && s.Type == type)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DbSegment>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.DbSegment.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DbSegment> AddAsync(DbSegment segment, CancellationToken cancellationToken = default)
    {
        segment.CreatedAt = DateTime.UtcNow;
        segment.UpdatedAt = DateTime.UtcNow;
        _dbContext.DbSegment.Add(segment);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return segment;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(DbSegment segment, CancellationToken cancellationToken = default)
    {
        segment.UpdatedAt = DateTime.UtcNow;
        _dbContext.DbSegment.Update(segment);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await _dbContext.DbSegment
            .Where(s => s.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteByItemIdAndTypeAsync(Guid itemId, AnalysisMode type, CancellationToken cancellationToken = default)
    {
        await _dbContext.DbSegment
            .Where(s => s.ItemId == itemId && s.Type == type)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await _dbContext.DbSegment
            .Where(s => s.ItemId == itemId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteBySeasonIdAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        await _dbContext.DbSegment
            .Where(s => s.SeasonId == seasonId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteOrphanedSegmentsAsync(IEnumerable<Guid> validItemIds, CancellationToken cancellationToken = default)
    {
        var validIds = validItemIds.ToList();

        await _dbContext.DbSegment
            .Where(s => !validIds.Contains(s.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetAnalyzedEpisodeIdsBySeasonAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var segments = await _dbContext.DbSegment
            .Where(s => s.SeasonId == seasonId)
            .Select(s => new { s.ItemId, s.Type })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return segments
            .GroupBy(s => s.Type)
            .ToDictionary(
                g => g.Key,
                g => (IEnumerable<Guid>)[.. g.Select(s => s.ItemId).Distinct()]);
    }
}
