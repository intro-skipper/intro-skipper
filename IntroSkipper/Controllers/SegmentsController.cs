// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.SegmentChanges;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

/// <summary>
/// Plural segments API: every stored segment of an item is addressable by its id
/// (shared with the Jellyfin media segment row). Boundaries are seconds at this edge.
/// Supersedes the singular <c>Episode/{id}/Timestamps</c> endpoints. Elevation-gated
/// editor surface: reads return the stored view, unfiltered by the per-item disable
/// flag; playback clients read Jellyfin's native media segments instead. Mutations
/// commit through the durable segment-change coordinator: a change whose Jellyfin
/// projection does not apply synchronously answers <c>202 Accepted</c> with a
/// <see cref="SegmentChangeAcceptedResponse"/> and converges from the journal.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SegmentsController"/> class.
/// </remarks>
/// <param name="database">Segment database facade (reads only).</param>
/// <param name="segmentChange">Durable segment-change coordinator; owns every mutation.</param>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public class SegmentsController(
    IIntroSkipperDatabase database,
    ISegmentChange segmentChange) : ControllerBase
{
    private readonly IIntroSkipperDatabase _database = database;
    private readonly ISegmentChange _segmentChange = segmentChange;

    /// <summary>
    /// Gets all stored segments of an item, ordered by type and start time.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="includeSuppressed">Whether tombstoned (user-deleted automatic) segments are included.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The stored segments.</response>
    /// <response code="404">The item is not an episode or movie.</response>
    /// <returns>The segment list.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("Episode/{itemId}/Segments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<SegmentDto>>> GetSegments(
        [FromRoute] Guid itemId,
        [FromQuery] bool includeSuppressed = false,
        CancellationToken cancellationToken = default)
    {
        if (MediaItemHelper.FindSupported(itemId) is null)
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
    /// <response code="202">The segment committed but its Jellyfin projection is pending or skipped.</response>
    /// <response code="400">The boundaries or the segment type are invalid.</response>
    /// <response code="404">The item is not an episode or movie.</response>
    /// <returns>The created segment DTO.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Episode/{itemId}/Segments")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SegmentDto>> CreateSegment(
        [FromRoute] Guid itemId,
        [FromBody] CreateSegmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (MediaItemHelper.FindSupported(itemId) is null)
        {
            return NotFound();
        }

        // The enum-string converter also accepts raw integers, so an unmapped numeric
        // mode binds successfully; reject it here so no unservable row is ever committed.
        if (!AnalysisHelpers.IsSupported(request.Type))
        {
            return BadRequest($"Unknown segment type '{(int)request.Type}'.");
        }

        if (!TickConversions.TryFromSecondsRange(request.Start, request.End, out var startTicks, out var endTicks))
        {
            return BadRequest("Start must be non-negative and End must be after Start.");
        }

        var outcome = await _segmentChange
            .ApplyAsync(new AddUserSegmentIntent(itemId, request.Type, startTicks, endTicks), cancellationToken)
            .ConfigureAwait(false);
        ActionResult result = outcome switch
        {
            Accepted { Projection: ProjectionState.Applied } accepted => ToCreated(accepted.AffectedValues.Single()),
            Accepted accepted => SegmentChangeHttp.Accepted(accepted),
            // An identical active user segment already exists; report it like a create.
            Ignored ignored => ToCreated(ignored.AffectedValues.Single()),
            Rejected rejected => SegmentChangeHttp.Rejected(rejected),
            _ => throw new InvalidOperationException($"Unknown segment change outcome '{outcome}'.")
        };
        return result;

        CreatedAtActionResult ToCreated(SegmentValue value)
            => CreatedAtAction(nameof(GetSegments), new { itemId }, SegmentChangeHttp.ToDto(value));
    }

    /// <summary>
    /// Updates a segment's boundaries; the segment becomes user-provided. Moving a segment
    /// exactly onto another segment of the same mode merges the two: the occupant survives
    /// as the user segment and is returned.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="segmentId">Segment id.</param>
    /// <param name="request">New boundaries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated (or merged-into) segment.</response>
    /// <response code="202">The update committed but its Jellyfin projection is pending or skipped.</response>
    /// <response code="400">The boundaries are invalid.</response>
    /// <response code="404">The segment does not exist on this item or is suppressed.</response>
    /// <returns>The surviving segment DTO.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPut("Episode/{itemId}/Segments/{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SegmentDto>> UpdateSegment(
        [FromRoute] Guid itemId,
        [FromRoute] Guid segmentId,
        [FromBody] UpdateSegmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (MediaItemHelper.FindSupported(itemId) is null)
        {
            return NotFound();
        }

        if (!TickConversions.TryFromSecondsRange(request.Start, request.End, out var startTicks, out var endTicks))
        {
            return BadRequest("Start must be non-negative and End must be after Start.");
        }

        var outcome = await _segmentChange
            .ApplyAsync(new UpdateSegmentIntent(itemId, segmentId, startTicks, endTicks), cancellationToken)
            .ConfigureAwait(false);
        ActionResult result = outcome switch
        {
            Accepted { Projection: ProjectionState.Applied } accepted => Ok(SegmentChangeHttp.ToDto(accepted.AffectedValues.Single())),
            Accepted accepted => SegmentChangeHttp.Accepted(accepted),
            // The segment already carries the requested values; report it like an update.
            Ignored ignored => Ok(SegmentChangeHttp.ToDto(ignored.AffectedValues.Single())),
            Rejected rejected => SegmentChangeHttp.Rejected(rejected),
            _ => throw new InvalidOperationException($"Unknown segment change outcome '{outcome}'.")
        };
        return result;
    }

    /// <summary>
    /// Deletes a segment. Automatic segments are tombstoned so re-analysis does not
    /// re-add an overlapping automatic segment; user segments are removed permanently.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="segmentId">Segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="204">The segment was deleted.</response>
    /// <response code="202">The delete committed but its Jellyfin projection is pending or skipped.</response>
    /// <response code="404">The segment does not exist on this item or is already suppressed.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpDelete("Episode/{itemId}/Segments/{segmentId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteSegment(
        [FromRoute] Guid itemId,
        [FromRoute] Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        if (MediaItemHelper.FindSupported(itemId) is null)
        {
            return NotFound();
        }

        var outcome = await _segmentChange
            .ApplyAsync(new DeleteSegmentIntent(itemId, segmentId), cancellationToken)
            .ConfigureAwait(false);
        return outcome switch
        {
            Accepted { Projection: ProjectionState.Applied } => NoContent(),
            Accepted accepted => SegmentChangeHttp.Accepted(accepted),
            // Unknown on the item or already suppressed — the id addresses nothing deletable.
            Ignored => NotFound(),
            Rejected rejected => SegmentChangeHttp.Rejected(rejected),
            _ => throw new InvalidOperationException($"Unknown segment change outcome '{outcome}'.")
        };
    }

    /// <summary>
    /// Restores a tombstoned segment, making it active again with its original source.
    /// </summary>
    /// <param name="itemId">Item (episode or movie) id.</param>
    /// <param name="segmentId">Segment id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The restored segment.</response>
    /// <response code="202">The restore committed but its Jellyfin projection is pending or skipped.</response>
    /// <response code="404">The segment does not exist on this item or is not suppressed.</response>
    /// <returns>The restored segment DTO.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Episode/{itemId}/Segments/{segmentId}/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SegmentDto>> RestoreSegment(
        [FromRoute] Guid itemId,
        [FromRoute] Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        if (MediaItemHelper.FindSupported(itemId) is null)
        {
            return NotFound();
        }

        var outcome = await _segmentChange
            .ApplyAsync(new RestoreSegmentIntent(itemId, segmentId), cancellationToken)
            .ConfigureAwait(false);
        ActionResult result = outcome switch
        {
            Accepted { Projection: ProjectionState.Applied } accepted => Ok(SegmentChangeHttp.ToDto(accepted.AffectedValues.Single())),
            Accepted accepted => SegmentChangeHttp.Accepted(accepted),
            // Unknown on the item or not suppressed — the id addresses nothing restorable.
            Ignored => NotFound(),
            Rejected rejected => SegmentChangeHttp.Rejected(rejected),
            _ => throw new InvalidOperationException($"Unknown segment change outcome '{outcome}'.")
        };
        return result;
    }
}
