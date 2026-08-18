// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Plan-level seam between the durable journal and Jellyfin's database.</summary>
internal interface ISegmentProjectionAdapter
{
    /// <summary>Resolves an exact external target before authoritative commit.</summary>
    /// <param name="itemId">Expected owning item.</param>
    /// <param name="externalSegmentId">External segment ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The actual target, or <see langword="null"/> when absent.</returns>
    Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken);

    /// <summary>Atomically applies one immutable item plan.</summary>
    /// <param name="plan">Immutable item plan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task ApplyAsync(SegmentProjectionPlan plan, CancellationToken cancellationToken);
}
