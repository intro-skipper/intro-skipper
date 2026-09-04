// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using IntroSkipper.Manager;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Default <see cref="ISegmentProjectionAdapter"/>: every write goes through the
/// mirror (<see cref="MediaSegmentMirror.DeleteValidatedSegmentAsync"/> for foreign
/// rows and <see cref="MediaSegmentMirror.SyncItemAsync"/> for the item's own image),
/// so projection shares the single documented write path, its per-item stripes, and
/// its disabled-mirror signaling.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="JellyfinSegmentProjectionAdapter"/> class.
/// </remarks>
/// <param name="segmentStore">Direct store for Jellyfin's media segments; used for reads only.</param>
/// <param name="mirror">The shared locked mirror write path.</param>
/// <param name="logger">Application logger.</param>
internal sealed partial class JellyfinSegmentProjectionAdapter(
    IJellyfinSegmentStore segmentStore,
    MediaSegmentMirror mirror,
    ILogger<JellyfinSegmentProjectionAdapter> logger) : ISegmentProjectionAdapter
{
    /// <inheritdoc />
    public async Task<ExternalSegmentTarget?> ResolveExternalTargetAsync(Guid itemId, Guid externalSegmentId, CancellationToken cancellationToken)
    {
        var row = await segmentStore.FindSegmentAsync(externalSegmentId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new ExternalSegmentTarget(row.Id, row.ItemId, row.Type, row.StartTicks, row.EndTicks);
    }

    /// <inheritdoc />
    public async Task<ProjectionApplyOutcome> ApplyAsync(Guid itemId, IReadOnlyList<DbProjectionExternalOperation> externalOperations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(externalOperations);

        foreach (var operation in externalOperations)
        {
            // The validated shape travels inside the delete predicate, so a row
            // rewritten under its stable id since validation (an apply can run hours
            // later on backoff, or days later with mirroring off) is left alone:
            // deleting it would remove content the user never approved. An already
            // vanished row is an idempotent success either way, so dropping the
            // operation is terminal on purpose: retrying into the same mismatch
            // forever would wedge the item.
            var outcome = await mirror.DeleteValidatedSegmentAsync(
                itemId,
                operation.ExternalSegmentId,
                operation.ExpectedType,
                operation.StartTicks,
                operation.EndTicks,
                cancellationToken).ConfigureAwait(false);
            if (outcome == MirrorDeleteOutcome.MirroringDisabled)
            {
                return ProjectionApplyOutcome.MirroringDisabled;
            }

            if (outcome == MirrorDeleteOutcome.RowNotFound
                && await segmentStore.FindSegmentAsync(operation.ExternalSegmentId, cancellationToken).ConfigureAwait(false) is not null)
            {
                LogExternalDeleteSuperseded(logger, operation.ExternalSegmentId, itemId);
            }
        }

        // Converges the item's own rows from current truth. The disabled outcome
        // comes from the same policy read that gated the write, so a toggle racing
        // this apply can never complete work the mirror silently skipped.
        return await mirror.SyncItemAsync(itemId, cancellationToken).ConfigureAwait(false) == MirrorSyncOutcome.MirroringDisabled
            ? ProjectionApplyOutcome.MirroringDisabled
            : ProjectionApplyOutcome.Applied;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Journaled delete of external segment {SegmentId} on item {ItemId} was dropped: the row no longer matches its validated shape.")]
    private static partial void LogExternalDeleteSuperseded(ILogger logger, Guid segmentId, Guid itemId);
}
