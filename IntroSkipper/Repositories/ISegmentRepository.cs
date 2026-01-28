// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.Repositories;

/// <summary>
/// Repository interface for segment data access.
/// </summary>
public interface ISegmentRepository
{
    /// <summary>
    /// Gets a segment by id.
    /// </summary>
    /// <param name="id">The primary key identifier of the segment to retrieve.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that resolves to the <see cref="DbSegment"/> if found; otherwise null.</returns>
    Task<DbSegment?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all segments for an item.
    /// </summary>
    /// <param name="itemId">The id of the media item to retrieve segments for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that resolves to a read-only list of <see cref="DbSegment"/> instances for the item.</returns>
    Task<IReadOnlyList<DbSegment>> GetByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all segments for a season.
    /// </summary>
    /// <param name="seasonId">The id of the season to retrieve segments for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that resolves to a read-only list of <see cref="DbSegment"/> instances for the season.</returns>
    Task<IReadOnlyList<DbSegment>> GetBySeasonIdAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all segments for an item filtered by type.
    /// </summary>
    /// <param name="itemId">The id of the media item to retrieve segments for.</param>
    /// <param name="type">The analysis mode (segment type) to filter by.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that resolves to a read-only list of <see cref="DbSegment"/> instances matching the filter.</returns>
    Task<IReadOnlyList<DbSegment>> GetByItemIdAndTypeAsync(Guid itemId, AnalysisMode type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all segments.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that resolves to a read-only list of all <see cref="DbSegment"/> instances.</returns>
    Task<IReadOnlyList<DbSegment>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new segment.
    /// </summary>
    /// <param name="segment">The <see cref="DbSegment"/> to add to the repository.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that resolves to the added <see cref="DbSegment"/> (including any database assigned id).</returns>
    Task<DbSegment> AddAsync(DbSegment segment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing segment.
    /// </summary>
    /// <param name="segment">The <see cref="DbSegment"/> with updated values to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task UpdateAsync(DbSegment segment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a segment by id.
    /// </summary>
    /// <param name="id">The id of the segment to delete.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments for an item by type.
    /// </summary>
    /// <param name="itemId">The id of the media item whose segments should be deleted.</param>
    /// <param name="type">The analysis mode (segment type) to delete.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteByItemIdAndTypeAsync(Guid itemId, AnalysisMode type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments for an item.
    /// </summary>
    /// <param name="itemId">The id of the media item whose segments should be deleted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments for a season.
    /// </summary>
    /// <param name="seasonId">The id of the season whose segments should be deleted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteBySeasonIdAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments not in the provided item ids.
    /// </summary>
    /// <param name="validItemIds">A collection of item ids that should be preserved; segments for items not in this collection will be deleted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteOrphanedSegmentsAsync(IEnumerable<Guid> validItemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets analyzed episode IDs grouped by analysis mode for a season.
    /// </summary>
    /// <param name="seasonId">The season ID to get analyzed episodes for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A dictionary mapping analysis modes to collections of analyzed episode IDs.</returns>
    Task<IReadOnlyDictionary<AnalysisMode, IEnumerable<Guid>>> GetAnalyzedEpisodeIdsBySeasonAsync(Guid seasonId, CancellationToken cancellationToken = default);
}
