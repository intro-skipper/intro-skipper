// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;

namespace IntroSkipper.Repositories;

/// <summary>
/// Repository interface for season configuration data access.
/// </summary>
public interface ISeasonRepository
{
    /// <summary>
    /// Gets the analyzer action for a season and mode.
    /// </summary>
    /// <param name="seasonId">The unique identifier of the season to get the analyzer action for.</param>
    /// <param name="mode">The <see cref="AnalysisMode"/> for which the analyzer action is requested.</param>
    /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> to cancel the operation.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation. The task result is the
    /// <see cref="AnalyzerAction"/> associated with the specified season and analysis mode.
    /// </returns>
    Task<AnalyzerAction> GetAnalyzerActionAsync(Guid seasonId, AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets analyzer actions for a season.
    /// </summary>
    /// <param name="seasonId">The unique identifier of the season to update.</param>
    /// <param name="actions">A read-only dictionary mapping <see cref="AnalysisMode"/> values to the desired <see cref="AnalyzerAction"/> for that mode.</param>
    /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetAnalyzerActionsAsync(Guid seasonId, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> actions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes season info for seasons not in the provided list.
    /// </summary>
    /// <param name="validSeasonIds">A collection of season IDs that should be kept; any seasons not present in this collection may be deleted.</param>
    /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteOrphanedSeasonsAsync(IEnumerable<Guid> validSeasonIds, CancellationToken cancellationToken = default);
}
