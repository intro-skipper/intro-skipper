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
/// Repository implementation for season configuration data access.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SeasonRepository"/> class.
/// </remarks>
/// <param name="dbContext">Database context.</param>
public class SeasonRepository(IntroSkipperDbContext dbContext) : ISeasonRepository
{
    private readonly IntroSkipperDbContext _dbContext = dbContext;

    /// <inheritdoc/>
    public async Task<AnalyzerAction> GetAnalyzerActionAsync(Guid seasonId, AnalysisMode mode, CancellationToken cancellationToken = default)
    {
        var seasonInfo = await _dbContext.DbSeasonInfo
            .FirstOrDefaultAsync(s => s.SeasonId == seasonId && s.Type == mode, cancellationToken)
            .ConfigureAwait(false);

        return seasonInfo?.Action ?? AnalyzerAction.Default;
    }

    /// <inheritdoc/>
    public async Task SetAnalyzerActionsAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> actions, CancellationToken cancellationToken = default)
    {
        var existingEntries = await _dbContext.DbSeasonInfo
            .Where(s => s.SeasonId == seasonId)
            .ToDictionaryAsync(s => s.Type, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (mode, action) in actions)
        {
            if (existingEntries.TryGetValue(mode, out var existing))
            {
                _dbContext.Entry(existing).Property(s => s.Action).CurrentValue = action;
            }
            else
            {
                _dbContext.DbSeasonInfo.Add(new DbSeasonInfo(seasonId, mode, action));
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteOrphanedSeasonsAsync(IEnumerable<Guid> validSeasonIds, CancellationToken cancellationToken = default)
    {
        var validIds = validSeasonIds.ToList();

        await _dbContext.DbSeasonInfo
            .Where(s => !validIds.Contains(s.SeasonId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
