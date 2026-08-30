// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Seam between the durable journal and Jellyfin's database. Applying projects
/// current truth: the journaled foreign-row deletes first, then the item's mirror
/// convergence — never a stored image, so a late apply cannot push a stale snapshot.
/// </summary>
internal interface ISegmentProjectionAdapter
{
    /// <summary>Resolves an exact external target before authoritative commit.</summary>
    /// <param name="itemId">Expected owning item.</param>
    /// <param name="externalSegmentId">External segment ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The actual target, or <see langword="null"/> when absent.</returns>
    Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies one item's pending work: the journaled foreign-row deletes in order,
    /// then the item's mirror convergence. A disabled mirror is reported as
    /// <see cref="ProjectionApplyOutcome.MirroringDisabled"/> — learned from the write
    /// path itself, so no re-read of the flag can race the decision. Throws to signal
    /// a real failure; every step is idempotent, so a partially applied attempt
    /// replays safely.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="externalOperations">Journaled foreign-row deletes, in FIFO order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the work was applied or must stay pending for the enable replay.</returns>
    Task<ProjectionApplyOutcome> ApplyAsync(Guid itemId, IReadOnlyList<ProjectedExternalOperation> externalOperations, CancellationToken cancellationToken);
}
