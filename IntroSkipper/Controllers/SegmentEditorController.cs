// SPDX-FileCopyrightText: 2025-2026 AbandonedCart
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

/// <summary>
/// Extended API for MediaSegments Management.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SegmentEditorController"/> class.
/// </remarks>
/// <param name="mediaSegmentEditorService">Media segment editor service; owns every mutation end-to-end.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(MediaSegmentEditorService mediaSegmentEditorService) : ControllerBase
{
    private readonly MediaSegmentEditorService _mediaSegmentEditorService = mediaSegmentEditorService;

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
    /// <returns>The created segment.</returns>
    [HttpPost("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
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

        // Legacy wire contract: a non-commercial POST replaces the mode's stored
        // segments with the posted one (clients edit by re-POSTing a new range), while
        // commercials — inherently many per item — are added, deduplicated on an
        // exact-range collision.
        if (mode == AnalysisMode.Commercial)
        {
            await _mediaSegmentEditorService
                .CreateUserSegmentAsync(itemId, mode, segment.StartTicks, segment.EndTicks, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _mediaSegmentEditorService
                .ReplaceUserSegmentsAsync(
                    itemId,
                    new Dictionary<AnalysisMode, (long StartTicks, long EndTicks)> { [mode] = (segment.StartTicks, segment.EndTicks) },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return Ok();
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
    /// delete converges the item's mirror so a re-added Jellyfin row disappears — 400
    /// when the requested type does not match the segment, or 404 when no segment is
    /// found. A segment id owned by a different item is rejected without mutating
    /// either item.
    /// </returns>
    [HttpDelete("{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
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

        // The service resolves the id (shared-id plugin row vs uncorrelated Jellyfin
        // row) under the item's mutation stripe, so the dispatch cannot race a
        // concurrent mutation.
        var result = await _mediaSegmentEditorService
            .DeleteLegacySegmentAsync(itemId, requestedMode, segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (result.MismatchedType is { } actualType)
        {
            return BadRequest($"Segment '{segmentId}' is {actualType}, not requested type '{type}'.");
        }

        return result.IsDeleted ? Ok() : NotFound();
    }
}
