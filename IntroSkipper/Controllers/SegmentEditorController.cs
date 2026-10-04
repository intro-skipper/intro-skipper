// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2025-2026 AbandonedCart
// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.SegmentChanges;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

/// <summary>
/// Extended API for MediaSegments Management. Mutations commit through the durable
/// segment-change coordinator: a change whose Jellyfin projection does not apply
/// synchronously answers <c>202 Accepted</c> and converges from the journal.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SegmentEditorController"/> class.
/// </remarks>
/// <param name="segmentChange">Durable segment-change coordinator; owns every mutation.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(SegmentChange segmentChange) : ControllerBase
{
    private readonly SegmentChange _segmentChange = segmentChange;

    /// <summary>
    /// Plugin meta endpoint.
    /// </summary>
    /// <returns>Plugin version metadata.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public JsonResult GetPluginMetadata()
    {
        var json = new
        {
            version = Plugin.Instance!.Version.ToString(4),
        };

        return new JsonResult(json);
    }

    /// <summary>
    /// Create MediaSegment for itemId.
    /// </summary>
    /// <param name="itemId">The ItemId.</param>
    /// <param name="providerId">Provider of the Segment.</param>
    /// <param name="segment">MediaSegment data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTTP 200 when the change applied synchronously, 202 when it committed with a pending or skipped projection.</returns>
    [HttpPost("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QueryResult<MediaSegmentDto>>> CreateSegmentAsync(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Required] string providerId,
        [FromBody, Required] MediaSegmentDto segment,
        CancellationToken cancellationToken = default)
    {
        if (MediaItemHelper.FindSupported(itemId) is null)
        {
            return NotFound();
        }

        if (!TickConversions.IsValidTickRange(segment.StartTicks, segment.EndTicks))
        {
            return BadRequest("EndTicks must be after StartTicks and both must be non-negative.");
        }

        // Unknown is a defined MediaSegmentType with no mode mapping and the default
        // when the body omits Type; reject it like every other unmapped type.
        if (AnalysisHelpers.TryMapSegmentTypeToMode(segment.Type) is not { } mode)
        {
            return BadRequest($"Unknown segment type '{segment.Type}'.");
        }

        // POST is the backwards-compatible single-segment form. Every media segment
        // type is additive now; callers that own the complete image should use PUT.
        // This keeps older clients that send one POST per row from deleting the rows
        // posted immediately before it.
        SegmentChangeIntent intent = new AddUserSegmentIntent(itemId, mode, segment.StartTicks, segment.EndTicks);
        var outcome = await _segmentChange.ApplyAsync(intent, cancellationToken).ConfigureAwait(false);
        // An already-stored image (an idempotent re-POST) answers like a fresh one.
        return SegmentChangeHttp.Map(outcome, onApplied: _ => Ok());
    }

    /// <summary>
    /// Replaces the complete MediaSegments image for an item atomically.
    /// </summary>
    /// <param name="itemId">The ItemId.</param>
    /// <param name="segments">The complete desired segment list, in Jellyfin ticks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTTP 200 when the change and projection applied synchronously, or 202 when the durable projection is pending/skipped.</returns>
    [HttpPut("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<MediaSegmentDto>>> ReplaceSegmentsAsync(
        [FromRoute, Required] Guid itemId,
        [FromBody, Required] MediaSegmentDto[] segments,
        CancellationToken cancellationToken = default)
    {
        if (MediaItemHelper.FindSupported(itemId) is null)
        {
            return NotFound();
        }

        var inputs = new List<UserSegmentInput>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment is null)
            {
                return BadRequest("Every segment must be non-null.");
            }

            if (segment.ItemId != Guid.Empty && segment.ItemId != itemId)
            {
                return BadRequest("Every segment must belong to the requested item.");
            }

            if (AnalysisHelpers.TryMapSegmentTypeToMode(segment.Type) is not { } mode)
            {
                return BadRequest($"Unknown segment type '{segment.Type}'.");
            }

            if (!TickConversions.IsValidTickRange(segment.StartTicks, segment.EndTicks))
            {
                return BadRequest("EndTicks must be after StartTicks and both must be non-negative.");
            }

            inputs.Add(new UserSegmentInput(
                segment.Id == Guid.Empty ? null : segment.Id,
                mode,
                segment.StartTicks,
                segment.EndTicks));
        }

        var outcome = await _segmentChange
            .ApplyAsync(new ReplaceUserSegmentsForItemIntent(itemId, inputs), cancellationToken)
            .ConfigureAwait(false);
        return SegmentChangeHttp.Map(
            outcome,
            onApplied: values => Ok(values.Select(ToMediaSegment).ToList()));

        static MediaSegmentDto ToMediaSegment(SegmentValue value) => new()
        {
            Id = value.Id,
            ItemId = value.ItemId,
            Type = AnalysisHelpers.ModeToSegmentType[value.Mode],
            StartTicks = value.StartTicks,
            EndTicks = value.EndTicks,
        };
    }

    /// <summary>
    /// Delete MediaSgment by segment id.
    /// </summary>
    /// <param name="segmentId">The Id of the media segment to delete.</param>
    /// <param name="itemId">The item id that owns the segment; scopes both the plugin DB row and the Jellyfin delete.</param>
    /// <param name="type">The media segment type name (Intro/Recap/Preview/Outro).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// HTTP 200 on success — including a row the plugin already tombstoned, where the
    /// journaled re-projection converges the item's mirror so a re-added Jellyfin row
    /// disappears — 202 when the delete committed with a pending or skipped
    /// projection, 400 when the requested type does not match the segment, or 404
    /// when no segment is found. A segment id owned by a different item is rejected
    /// without mutating either item.
    /// </returns>
    [HttpDelete("{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteSegmentAsync(
        [FromRoute, Required] Guid segmentId,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string type,
        CancellationToken cancellationToken = default)
    {
        // "credits" is a legacy wire alias for the Outro segment type.
        AnalysisMode? parsedMode = type.Equals("credits", StringComparison.OrdinalIgnoreCase)
            ? AnalysisMode.Credits
            : AnalysisHelpers.TryParseSegmentTypeName(type);
        if (parsedMode is not { } requestedMode)
        {
            return BadRequest($"Unknown segment type '{type}'.");
        }

        // The coordinator resolves the id (shared-id plugin row vs uncorrelated
        // Jellyfin row) inside the intent transaction, so the dispatch cannot race a
        // concurrent mutation.
        var outcome = await _segmentChange
            .ApplyAsync(new EditorDeleteSegmentIntent(itemId, segmentId, AnalysisHelpers.ModeToSegmentType[requestedMode]), cancellationToken)
            .ConfigureAwait(false);
        // A row the plugin already treats as deleted answers like a fresh delete; the
        // journaled re-projection (or the still-pending journaled delete) converges Jellyfin.
        return SegmentChangeHttp.Map(outcome, onApplied: _ => Ok());
    }
}
