// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// Persistence operations for media segments (<see cref="DbSegment"/>). Pure storage: business
/// rules such as user-provided precedence live in the domain layer
/// (<see cref="IntroSkipper.Services.ISegmentUpdateService"/>), which passes decisions into
/// <see cref="ReplaceNonCommercialAsync"/> so they execute inside the store's write transaction.
/// </summary>
public interface ISegmentStore
{
    /// <summary>
    /// Gets all stored segments for an item.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stored segments for the item.</returns>
    Task<IReadOnlyList<DbSegment>> GetSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the earliest stored segment per analysis mode for an item.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Segments keyed by analysis mode.</returns>
    Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a commercial segment unless an equivalent one (same item, start and end within
    /// <paramref name="epsilon"/>) already exists.
    /// </summary>
    /// <param name="segment">Commercial segment to insert.</param>
    /// <param name="epsilon">Tolerance used when comparing start/end times.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the segment was inserted; <see langword="false"/> when an equivalent segment already existed.</returns>
    Task<bool> TryAddCommercialAsync(DbSegment segment, double epsilon, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces the stored segments for the candidate's item and mode with the candidate,
    /// but only when <paramref name="shouldPersist"/> approves the write. The callback runs inside
    /// the write transaction with a snapshot of the existing rows, keeping the domain decision and
    /// the write atomic.
    /// </summary>
    /// <param name="segment">Candidate segment (non-commercial).</param>
    /// <param name="shouldPersist">Domain decision invoked with the stored state; return <see langword="false"/> to skip the write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the segment was persisted; <see langword="false"/> when the write was skipped.</returns>
    Task<bool> ReplaceNonCommercialAsync(DbSegment segment, Func<NonCommercialSegmentContext, bool> shouldPersist, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments for an item.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the segments for an item and analysis mode, optionally restricted to entries whose
    /// start/end match <paramref name="match"/> within <paramref name="epsilon"/>.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="match">Optional segment whose start/end restrict the delete.</param>
    /// <param name="epsilon">Tolerance used when comparing start/end times.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteSegmentsAsync(Guid itemId, AnalysisMode mode, Segment? match, double epsilon, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all segments of the given analysis mode.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteSegmentsByTypeAsync(AnalysisMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes segments belonging to items that are not in <paramref name="enabledItemIds"/>.
    /// Safe for arbitrarily large ID sets: the collection is sent as a single JSON parameter.
    /// </summary>
    /// <param name="enabledItemIds">Item IDs whose segments should be kept.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanTimestampsAsync(IReadOnlyCollection<Guid> enabledItemIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes stale automatic segments (config hash mismatch) for the supplied items and mode.
    /// User-provided segments are intentionally preserved. Safe for arbitrarily large ID sets.
    /// </summary>
    /// <param name="itemIds">Item IDs to inspect.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="configHash">Current configuration hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task CleanStaleAutomaticSegmentsAsync(IReadOnlyCollection<Guid> itemIds, AnalysisMode mode, string configHash, CancellationToken cancellationToken = default);
}
