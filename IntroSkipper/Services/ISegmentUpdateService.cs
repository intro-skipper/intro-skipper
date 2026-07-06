// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Services;

/// <summary>
/// Domain service owning the business rules for writing and deleting media segments:
/// user-provided segments take precedence over analysis results, auto-detected credits must not
/// overlap the stored introduction, and commercial segments are deduplicated within a tolerance.
/// Persistence is delegated to <see cref="IntroSkipper.Db.ISegmentStore"/>.
/// </summary>
public interface ISegmentUpdateService
{
    /// <summary>
    /// Stores a segment for an item, enforcing the domain rules above.
    /// </summary>
    /// <param name="segment">Segment to store.</param>
    /// <param name="mode">Analysis mode that produced the segment.</param>
    /// <param name="isUserProvided">Whether the segment was provided by the user via the segment editor.</param>
    /// <param name="configHash">Configuration hash that produced the segment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task UpdateTimestampAsync(Segment segment, AnalysisMode mode, bool isUserProvided = false, string configHash = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a stored timestamp for the specified item and analysis mode. Commercial deletions
    /// require an explicit <paramref name="segment"/> because multiple commercials may exist per item.
    /// </summary>
    /// <param name="itemId">The item ID whose timestamp should be removed.</param>
    /// <param name="mode">The analysis mode representing the segment type.</param>
    /// <param name="segment">Optional segment details used to remove a specific entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteTimestampAsync(Guid itemId, AnalysisMode mode, Segment? segment = null, CancellationToken cancellationToken = default);
}
