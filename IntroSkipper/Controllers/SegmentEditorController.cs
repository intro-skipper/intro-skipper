// SPDX-FileCopyrightText: 2025-2026 AbandonedCart
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Db;
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
/// <param name="database">Segment database facade.</param>
/// <param name="segmentStore">Direct store for Jellyfin's media segments, for reads outside the mutation path.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(
    MediaSegmentEditorService mediaSegmentEditorService,
    IIntroSkipperDatabase database,
    IJellyfinSegmentStore segmentStore) : ControllerBase
{
    private readonly MediaSegmentEditorService _mediaSegmentEditorService = mediaSegmentEditorService;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;

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

        if (segment.StartTicks < 0 || segment.EndTicks <= segment.StartTicks)
        {
            return BadRequest("EndTicks must be after StartTicks and both must be non-negative.");
        }

        // Unknown is a defined MediaSegmentType with no mode mapping and the default
        // when the body omits Type; reject it like every other unmapped type.
        if (AnalysisHelpers.TryMapSegmentTypeToMode(segment.Type) is not { } mode)
        {
            return BadRequest($"Unknown segment type '{segment.Type}'.");
        }

        await _mediaSegmentEditorService
            .CreateUserSegmentAsync(itemId, mode, segment.StartTicks, segment.EndTicks, cancellationToken)
            .ConfigureAwait(false);

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
    /// HTTP 200 on success, 400 when the requested type does not match the Jellyfin segment,
    /// or 404 when the commercial segment is not found. A segment id owned by a different item
    /// is rejected without mutating either item.
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

        // Fast path: the plugin row shares the Jellyfin row's id.
        var pluginRow = await _database.GetSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
        if (pluginRow is not null && pluginRow.ItemId == itemId)
        {
            if (pluginRow.Type != requestedMode)
            {
                return BadRequest($"Segment '{segmentId}' is {AnalysisHelpers.ModeToSegmentType[pluginRow.Type]}, not requested type '{type}'.");
            }

            // Deletability (unknown, vanished, or suppressed rows → 404) is the cascade's
            // call, derived from its result exactly like the plural DELETE endpoint.
            var deleted = await _mediaSegmentEditorService
                .DeleteSegmentAsync(itemId, segmentId, cancellationToken)
                .ConfigureAwait(false);
            if (deleted is null)
            {
                return NotFound();
            }
        }
        else
        {
            // Fallback for uncorrelated ids: rows Jellyfin materialized before the shared-id
            // scheme, or foreign-provider rows. The cascade matches the plugin-side
            // counterpart by exact ticks; without one, only the Jellyfin row is removed
            // and the state still resets.
            var existingSegment = await _segmentStore
                .GetSegmentAsync(itemId, segmentId, cancellationToken)
                .ConfigureAwait(false);
            if (existingSegment is null)
            {
                return NotFound();
            }

            if (existingSegment.Type != AnalysisHelpers.ModeToSegmentType[requestedMode])
            {
                return BadRequest($"Segment '{segmentId}' is {existingSegment.Type}, not requested type '{type}'.");
            }

            await _mediaSegmentEditorService
                .DeleteUncorrelatedSegmentAsync(itemId, requestedMode, existingSegment, cancellationToken)
                .ConfigureAwait(false);
        }

        return Ok();
    }
}
