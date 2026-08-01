// SPDX-FileCopyrightText: 2025-2026 AbandonedCart
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Db;
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
/// <param name="mediaSegmentEditorService">Media segment editor service.</param>
/// <param name="database">Segment database facade.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(MediaSegmentEditorService mediaSegmentEditorService, IIntroSkipperDatabase database) : ControllerBase
{
    private readonly MediaSegmentEditorService _mediaSegmentEditorService = mediaSegmentEditorService;
    private readonly IIntroSkipperDatabase _database = database;

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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QueryResult<MediaSegmentDto>>> CreateSegmentAsync(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Required] string providerId,
        [FromBody, Required] MediaSegmentDto segment,
        CancellationToken cancellationToken = default)
    {
        var item = Plugin.Instance!.GetItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (segment.StartTicks < 0 || segment.EndTicks <= segment.StartTicks)
        {
            return BadRequest("EndTicks must be after StartTicks and both must be non-negative.");
        }

        var mode = AnalysisHelpers.MapSegmentTypeToMode(segment.Type);

        await _database.AddUserSegmentAsync(itemId, mode, segment.StartTicks, segment.EndTicks, cancellationToken).ConfigureAwait(false);
        await _mediaSegmentEditorService.SyncItemAsync(item, cancellationToken).ConfigureAwait(false);

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

            if (pluginRow.State == SegmentState.Suppressed)
            {
                return NotFound();
            }

            await _mediaSegmentEditorService
                .DeleteStoredSegmentAsync(itemId, requestedMode, segmentId, segmentId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Fallback for uncorrelated ids: rows Jellyfin materialized before the shared-id
            // scheme, or foreign-provider rows. Match the plugin-side counterpart by exact ticks.
            var existingSegment = await _mediaSegmentEditorService
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

            var itemRows = await _database.GetSegmentsAsync(itemId, cancellationToken: cancellationToken).ConfigureAwait(false);
            var match = itemRows.FirstOrDefault(s => s.Type == requestedMode
                && s.StartTicks == existingSegment.StartTicks
                && s.EndTicks == existingSegment.EndTicks);

            // The uncorrelated fallback matches the plugin counterpart by exact ticks;
            // without one, only the Jellyfin row is removed and the state still resets.
            await _mediaSegmentEditorService
                .DeleteStoredSegmentAsync(itemId, requestedMode, segmentId, match?.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return Ok();
    }
}
