// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;
using IntroSkipper.Providers;
using IntroSkipper.SegmentChanges;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// The plugin's write path into Jellyfin's media segments, driven by
/// <see cref="SegmentChange"/>: <see cref="ApplyAsync"/> runs an item's journaled
/// foreign-row deletes, then converges the item's own rows on the plugin database.
/// Convergence never touches other providers' segments; a journaled delete removes any
/// of the item's rows by id (the editor lets users delete foreign rows). Every write
/// no-ops when mirroring is disabled (<see cref="IMediaSegmentMirrorPolicy"/>), so
/// callers never gate it. The one writer that bypasses this class is Jellyfin itself:
/// it persists <see cref="SegmentProvider"/> results during its own provider runs and
/// can therefore re-add a just-deleted segment from a read that predates the delete,
/// until a later apply converges the item.
/// </summary>
/// <remarks>
/// Not locked: the plugin-database read, the mirror comparison and the Jellyfin replace
/// must run as one unit per item, or a write derived from a stale read could land after
/// a newer one. <see cref="SegmentChange"/>, the only caller, provides that by holding
/// the item's mutation stripe around every apply.
/// </remarks>
/// <param name="segmentStore">The direct store for Jellyfin's media segments.</param>
/// <param name="segmentDtoFactory">The factory that converts stored plugin segments to Jellyfin DTOs.</param>
/// <param name="policy">The mirroring flag, read before every write.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class MediaSegmentMirror(
    IJellyfinSegmentStore segmentStore,
    SegmentDtoFactory segmentDtoFactory,
    IMediaSegmentMirrorPolicy policy,
    ILogger<MediaSegmentMirror> logger)
{
    /// <summary>
    /// Finds a Jellyfin segment by id alone, any item and any provider. Reads are never
    /// gated by the mirroring flag.
    /// </summary>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segment, or <see langword="null"/> when no row has the id.</returns>
    public Task<MediaSegmentDto?> FindSegmentAsync(Guid segmentId, CancellationToken cancellationToken)
        => segmentStore.FindSegmentAsync(segmentId, cancellationToken);

    /// <summary>
    /// Applies one item's projection work: the journaled foreign-row deletes in order,
    /// then the item's convergence. Convergence pushes every active plugin segment
    /// (carrying its plugin row id) and removes Intro Skipper rows no longer in the plugin
    /// database; when Jellyfin's rows already equal the intended push the replace is
    /// skipped, so bulk refreshes over unchanged items stay read-only instead of taking
    /// one write transaction each under Jellyfin's database lock.
    /// </summary>
    /// <remarks>
    /// Each delete carries its validated type and boundaries inside the delete predicate,
    /// so a row rewritten under its stable id since validation is left alone: deleting it
    /// would remove content the user never approved. That drop and an already vanished
    /// row both count as done, because retrying into the same mismatch would wedge the
    /// item. Every step is idempotent, so a partially applied attempt replays safely.
    /// </remarks>
    /// <param name="itemId">The item id.</param>
    /// <param name="externalOperations">The journaled foreign-row deletes, in FIFO order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when Jellyfin converged on the item's current truth;
    /// <see langword="false"/> when mirroring is disabled and the rest of the work was not
    /// attempted. The disabled answer comes from the same check that gated the write, so
    /// the durable projection never reports unpushed work as done.</returns>
    public async Task<bool> ApplyAsync(Guid itemId, IReadOnlyList<DbProjectionExternalOperation> externalOperations, CancellationToken cancellationToken)
    {
        foreach (var operation in externalOperations)
        {
            if (!policy.Enabled)
            {
                return false;
            }

            var deleted = await segmentStore.DeleteValidatedSegmentAsync(
                itemId,
                operation.ExternalSegmentId,
                operation.ExpectedType,
                operation.StartTicks,
                operation.EndTicks,
                cancellationToken).ConfigureAwait(false);
            if (deleted == 0
                && await segmentStore.FindSegmentAsync(operation.ExternalSegmentId, cancellationToken).ConfigureAwait(false) is not null)
            {
                LogExternalDeleteSuperseded(logger, operation.ExternalSegmentId, itemId);
            }
        }

        if (!policy.Enabled)
        {
            return false;
        }

        var segments = await segmentDtoFactory.CreateAsync(itemId, cancellationToken).ConfigureAwait(false);
        var mirrored = await segmentStore.GetOwnSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (!SegmentsMatch(mirrored, segments))
        {
            await segmentStore.ReplaceSegmentsAsync(itemId, segments, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // (Id, StartTicks, EndTicks, Type) plus the query-fixed item and provider id is the
    // entire surface the store writes, so with ids unique per row an equal-count subset
    // check is an exact row-set match. Any drift, including rows Jellyfin should hold
    // but does not, fails the match and triggers the full replace, so the skip never
    // costs the sync its self-healing.
    private static bool SegmentsMatch(IReadOnlyList<MediaSegmentDto> mirrored, IReadOnlyList<MediaSegmentDto> desired)
    {
        if (mirrored.Count != desired.Count)
        {
            return false;
        }

        var mirroredRows = mirrored.Select(RowKey).ToHashSet();
        return desired.All(segment => mirroredRows.Contains(RowKey(segment)));

        static (Guid Id, long StartTicks, long EndTicks, MediaSegmentType Type) RowKey(MediaSegmentDto segment)
            => (segment.Id, segment.StartTicks, segment.EndTicks, segment.Type);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Journaled delete of external segment {SegmentId} on item {ItemId} was dropped: the row no longer matches its validated shape.")]
    private static partial void LogExternalDeleteSuperseded(ILogger logger, Guid segmentId, Guid itemId);
}
