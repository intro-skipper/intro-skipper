// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Commits authoritative segment changes and durably projects them into Jellyfin.
/// </summary>
public interface ISegmentChange
{
    /// <summary>
    /// Applies one closed segment-change intent. Once the intent is accepted the
    /// outcome reports the committed change even when the immediate projection could
    /// not run (cancellation included — the projection then reports
    /// <see cref="ProjectionState.Pending"/> and the retry worker owns the journaled
    /// work); only a failure to commit throws.
    /// </summary>
    /// <param name="intent">Closed domain intent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The typed domain and projection outcome.</returns>
    Task<SegmentChangeOutcome> ApplyAsync(SegmentChangeIntent intent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets aggregate projection state. Applied items hold no pending work and are not
    /// enumerated by the all-items scope; a one-item scope always answers.
    /// </summary>
    /// <param name="scope">All items or one item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregate status with per-item details and counts.</returns>
    Task<ProjectionStatus> GetProjectionStatusAsync(ProjectionScope scope, CancellationToken cancellationToken = default);

    /// <summary>Immediately retries pending projections in the requested scope.</summary>
    /// <param name="scope">All items or one item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Retry counts and resulting aggregate status.</returns>
    Task<ProjectionRetryOutcome> RetryProjectionAsync(ProjectionScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Immediately converges the given items' pending projection work, with bounded
    /// parallelism and no per-item status readback — the batch form maintenance
    /// writers use after a bulk erase. Empty and duplicate ids are ignored, an item
    /// without pending work is a cheap no-op, and anything a pass cannot finish (a
    /// failure, cancellation, disabled mirroring) stays journaled for the worker.
    /// </summary>
    /// <param name="itemIds">The items to converge.</param>
    /// <param name="cancellationToken">Cancellation token; stops the batch between items.</param>
    /// <returns>The number of items whose work applied.</returns>
    Task<int> ProjectItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken = default);
}
