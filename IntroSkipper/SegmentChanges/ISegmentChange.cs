// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Commits authoritative segment changes and durably projects them into Jellyfin.
/// </summary>
public interface ISegmentChange
{
    /// <summary>Applies one closed segment-change intent.</summary>
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
}
