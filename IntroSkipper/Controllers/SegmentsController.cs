// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

/// <summary>
/// Plural segments API: every stored segment of an item is addressable by its id
/// (shared with the Jellyfin media segment row). Boundaries are seconds at this edge.
/// Supersedes the singular <c>Episode/{id}/Timestamps</c> endpoints.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SegmentsController"/> class.
/// </remarks>
/// <param name="database">Segment database facade.</param>
/// <param name="mediaSegmentEditorService">Media segment editor service.</param>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public class SegmentsController(IIntroSkipperDatabase database, MediaSegmentEditorService mediaSegmentEditorService) : ControllerBase
{
    private readonly IIntroSkipperDatabase _database = database;
    private readonly MediaSegmentEditorService _mediaSegmentEditorService = mediaSegmentEditorService;

    /// <summary>
    /// Gets all stored segments of an item, ordered by type and start time.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="includeSuppressed">Whether tombstoned (user-deleted automatic) segments are included.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The stored segments.</response>
    /// <response code="404">The item is not an episode or movie.</response>
    /// <returns>The segment list.</returns>
    [HttpGet("Episode/{itemId}/Segments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<SegmentDto>>> GetSegments(
        [FromRoute] Guid itemId,
        [FromQuery] bool includeSuppressed = false,
        CancellationToken cancellationToken = default)
    {
        if (ResolveItem(itemId) is null)
        {
            return NotFound();
        }

        var segments = await _database.GetSegmentsAsync(itemId, includeSuppressed, cancellationToken).ConfigureAwait(false);
        return Ok(segments.Select(SegmentDto.FromDbSegment).ToList());
    }

    /// <summary>
    /// Creates a user segment. An exact-range collision with an existing segment is
    /// resolved in place (the existing row is promoted to a user segment).
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="request">Segment to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="201">The created segment.</response>
    /// <response code="400">The boundaries are invalid.</response>
    /// <response code="404">The item is not an episode or movie.</response>
    /// <returns>The created segment DTO.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Episode/{itemId}/Segments")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SegmentDto>> CreateSegment(
        [FromRoute] Guid itemId,
        [FromBody] CreateSegmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = ResolveItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (!TryConvertRange(request.Start, request.End, out var startTicks, out var endTicks))
        {
            return BadRequest("Start must be non-negative and End must be after Start.");
        }

        var row = await _database.AddUserSegmentAsync(itemId, request.Type, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
        await PushAsync(item, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetSegments), new { itemId }, SegmentDto.FromDbSegment(row));
    }

    /// <summary>
    /// Updates a segment's boundaries; the segment becomes user-provided.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="segmentId">Segment id.</param>
    /// <param name="request">New boundaries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated segment.</response>
    /// <response code="400">The boundaries are invalid.</response>
    /// <response code="404">The segment does not exist on this item or is suppressed.</response>
    /// <response code="409">Another segment of the same item and mode covers exactly the new range.</response>
    /// <returns>The updated segment DTO.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPut("Episode/{itemId}/Segments/{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SegmentDto>> UpdateSegment(
        [FromRoute] Guid itemId,
        [FromRoute] Guid segmentId,
        [FromBody] UpdateSegmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = ResolveItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (!TryConvertRange(request.Start, request.End, out var startTicks, out var endTicks))
        {
            return BadRequest("Start must be non-negative and End must be after Start.");
        }

        var existing = await _database.GetSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.ItemId != itemId)
        {
            return NotFound();
        }

        DbSegment? updated;
        try
        {
            updated = await _database.UpdateSegmentAsync(segmentId, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
        }
        catch (SegmentConflictException ex)
        {
            return Conflict(ex.Message);
        }

        if (updated is null)
        {
            return NotFound();
        }

        await PushAsync(item, cancellationToken).ConfigureAwait(false);
        return Ok(SegmentDto.FromDbSegment(updated));
    }

    /// <summary>
    /// Deletes a segment. Automatic segments are tombstoned so re-analysis does not
    /// re-add an overlapping automatic segment; user segments are removed permanently.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="segmentId">Segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="204">The segment was deleted.</response>
    /// <response code="404">The segment does not exist on this item or is already suppressed.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("Episode/{itemId}/Segments/{segmentId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteSegment(
        [FromRoute] Guid itemId,
        [FromRoute] Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        var item = ResolveItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var existing = await _database.GetSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.ItemId != itemId || existing.State == SegmentState.Suppressed)
        {
            return NotFound();
        }

        var result = await _database.DeleteSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
        if (result.Deleted is null)
        {
            return NotFound();
        }

        // Jellyfin is only a mirror: when segment updates are disabled it stays
        // untouched, consistent with create/update/restore. When enabled, the
        // targeted delete (rather than relying on the full sync below alone) gives a
        // precise failure point to roll the plugin delete back from.
        if (Plugin.Instance!.Configuration.UpdateMediaSegments)
        {
            try
            {
                // The Jellyfin row shares the segment id; an unknown id is a no-op there.
                await _mediaSegmentEditorService.DeleteSegmentAsync(itemId, segmentId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Rollback is deliberately uncancelable once the plugin delete has completed.
                await _database.UndoDeleteAsync(result, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        // Return the episode to NotAnalyzed for this mode so the next analysis run can
        // re-detect remaining segments (the tombstone keeps the deleted one gone).
        await _database.RemoveEpisodeIdAsync(SeasonStateKeyResolver.Resolve(item), existing.Type, itemId, cancellationToken).ConfigureAwait(false);
        await PushAsync(item, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Restores a tombstoned segment, making it active again with its original source.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="segmentId">Segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The restored segment.</response>
    /// <response code="404">The segment does not exist on this item or is not suppressed.</response>
    /// <returns>The restored segment DTO.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Episode/{itemId}/Segments/{segmentId}/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SegmentDto>> RestoreSegment(
        [FromRoute] Guid itemId,
        [FromRoute] Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        var item = ResolveItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var existing = await _database.GetSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.ItemId != itemId)
        {
            return NotFound();
        }

        if (!await _database.RestoreSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        var restored = await _database.GetSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
        await PushAsync(item, cancellationToken).ConfigureAwait(false);
        return restored is null ? NotFound() : Ok(SegmentDto.FromDbSegment(restored));
    }

    private static BaseItem? ResolveItem(Guid itemId)
    {
        var item = Plugin.Instance!.GetItem(itemId);
        return item is Episode or Movie ? item : null;
    }

    private static bool TryConvertRange(double startSeconds, double endSeconds, out long startTicks, out long endTicks)
    {
        endTicks = 0;
        return TickConversions.TryFromSeconds(startSeconds, out startTicks)
            && TickConversions.TryFromSeconds(endSeconds, out endTicks)
            && endTicks > startTicks;
    }

    private async Task PushAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (Plugin.Instance!.Configuration.UpdateMediaSegments)
        {
            await _mediaSegmentEditorService.SyncItemAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }
}
