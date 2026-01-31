// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.Services;

/// <summary>
/// Service interface for segment business logic.
/// </summary>
public interface ISegmentService
{
    /// <summary>
    /// Creates a new segment and queues sync to Jellyfin.
    /// </summary>
    /// <param name="segment">The segment data to create.</param>
    /// <param name="type">The analysis mode for the segment.</param>
    /// <param name="isFirstAppearance">Whether this is the first episode where this intro pattern was detected.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task<DbSegment> CreateSegmentAsync(Segment segment, AnalysisMode type, bool isFirstAppearance = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all segments for an item.
    /// </summary>
    /// <param name="itemId">The unique identifier of the media item to get segments for.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> that resolves to a read-only list of <see cref="DbSegment"/> instances.</returns>
    Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets segments for an item filtered by type.
    /// </summary>
    /// <param name="itemId">The unique identifier of the media item to get segments for.</param>
    /// <param name="type">The analysis mode used to filter segments.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> that resolves to a read-only list of <see cref="DbSegment"/> instances matching the given type.</returns>
    Task<IReadOnlyList<DbSegment>> GetSegmentsByTypeAsync(Guid itemId, AnalysisMode type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets segments as a dictionary grouped by type.
    /// </summary>
    /// <param name="itemId">The unique identifier of the media item to get segments for.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    /// A <see cref="Task"/> that resolves to a read-only dictionary mapping each <see cref="AnalysisMode"/> to
    /// the corresponding <see cref="Segment"/> for the given item.
    /// </returns>
    Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetSegmentsDictionaryAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing segment and queues sync to Jellyfin.
    /// </summary>
    /// <param name="segment">The database segment entity containing updated values.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task UpdateSegmentAsync(DbSegment segment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a segment and queues sync to Jellyfin.
    /// </summary>
    /// <param name="segmentId">The primary key of the segment to delete.</param>
    /// <param name="itemId">The identifier of the media item the segment belongs to.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteSegmentAsync(int segmentId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments of a specific type for an item and queues sync to Jellyfin.
    /// </summary>
    /// <param name="itemId">The unique identifier of the media item whose segments should be deleted.</param>
    /// <param name="type">The analysis mode of segments that should be deleted.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteSegmentsByTypeAsync(Guid itemId, AnalysisMode type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments for an item and queues sync to Jellyfin.
    /// </summary>
    /// <param name="itemId">The unique identifier of the media item whose segments should be deleted.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteAllSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments for a season and queues sync to Jellyfin for each affected item.
    /// </summary>
    /// <param name="seasonId">The unique identifier of the season whose segments should be deleted.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteSeasonSegmentsAsync(Guid seasonId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces all segments of a specific type for a season with new segments in a single transaction.
    /// This is an optimized batch operation that deletes old segments and inserts new ones,
    /// then queues a single outbox entry per affected item.
    /// </summary>
    /// <param name="seasonId">The unique identifier of the season.</param>
    /// <param name="type">The analysis mode of segments to replace.</param>
    /// <param name="analyzedSegments">The new segments to insert.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task ReplaceSeasonSegmentsAsync(Guid seasonId, AnalysisMode type, IEnumerable<AnalyzedSegment> analyzedSegments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes segments for items that no longer exist.
    /// Note: This does not queue outbox entries since the items no longer exist in Jellyfin.
    /// </summary>
    /// <param name="validItemIds">A collection of item IDs that are still valid and should be kept.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanupOrphanedSegmentsAsync(IEnumerable<Guid> validItemIds, CancellationToken cancellationToken = default);
}
