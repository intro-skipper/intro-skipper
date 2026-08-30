// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Manager;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Default <see cref="ISegmentProjectionAdapter"/>: every write goes through the
/// mirror — <see cref="MediaSegmentMirror.DeleteSegmentAsync"/> for foreign rows and
/// <see cref="MediaSegmentMirror.SyncItemAsync"/> for the item's own image — so
/// projection shares the single documented write path and its per-item stripes.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="JellyfinSegmentProjectionAdapter"/> class.
/// </remarks>
/// <param name="segmentStore">Direct store for Jellyfin's media segments; used for reads only.</param>
/// <param name="mirror">The shared locked mirror write path.</param>
/// <param name="mirrorPolicy">Live mirroring flag, re-checked after the sync so a
/// disable landing mid-apply cannot complete work the mirror silently skipped.</param>
/// <param name="logger">Application logger.</param>
internal sealed partial class JellyfinSegmentProjectionAdapter(
    IJellyfinSegmentStore segmentStore,
    MediaSegmentMirror mirror,
    IMediaSegmentMirrorPolicy mirrorPolicy,
    ILogger<JellyfinSegmentProjectionAdapter> logger) : ISegmentProjectionAdapter
{
    /// <inheritdoc />
    public async Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken)
    {
        var row = await segmentStore.FindSegmentAsync(externalSegmentId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new ExternalSegmentTarget(row.Id, row.ItemId, row.Type, row.StartTicks, row.EndTicks);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(Guid itemId, IReadOnlyList<ProjectedExternalOperation> externalOperations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(externalOperations);

        foreach (var operation in externalOperations)
        {
            var existing = await segmentStore.GetSegmentAsync(itemId, operation.ExternalSegmentId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                // Already gone (possibly by an earlier partial attempt); the delete
                // is idempotently satisfied.
                continue;
            }

            if (existing.Type != operation.ExpectedType
                || existing.StartTicks != operation.StartTicks
                || existing.EndTicks != operation.EndTicks)
            {
                // The row changed under its id since the delete was validated (an
                // apply can run hours later on backoff, or days later with mirroring
                // off); deleting it now would remove content the user never approved.
                // Dropping the operation is terminal on purpose — throwing would
                // retry into the same mismatch forever and wedge the item.
                LogExternalDeleteSuperseded(logger, operation.ExternalSegmentId, itemId);
                continue;
            }

            var outcome = await mirror.DeleteSegmentAsync(itemId, operation.ExternalSegmentId, cancellationToken).ConfigureAwait(false);
            if (outcome == MirrorDeleteOutcome.MirroringDisabled)
            {
                // Mirroring flipped off mid-apply; keep the work pending so the
                // enable replay runs it.
                throw new InvalidOperationException("Mirroring was disabled while the projection was being applied.");
            }
        }

        // Converges the item's own rows from current truth. The sync silently no-ops
        // when mirroring is disabled, so re-check the flag afterwards: observing it
        // disabled means the sync may not have pushed, and the work must stay pending
        // for the enable replay instead of being completed unpushed. (Observing it
        // enabled after a real push that a later disable follows merely retries an
        // idempotent sync.)
        await mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (!mirrorPolicy.Enabled)
        {
            throw new InvalidOperationException("Mirroring was disabled while the projection was being applied.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Journaled delete of external segment {SegmentId} on item {ItemId} was dropped: the row no longer matches its validated type.")]
    private static partial void LogExternalDeleteSuperseded(ILogger logger, Guid segmentId, Guid itemId);
}
