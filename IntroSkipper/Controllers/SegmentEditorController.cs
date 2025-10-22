using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Manager;
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
/// <param name="mediaSegmentUpdateManager">MediaSegmentUpdateManager.</param>
[Authorize(Policy = "RequiresElevation")]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(MediaSegmentUpdateManager mediaSegmentUpdateManager) : ControllerBase
{
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;

    /// <summary>
    /// Plugin meta endpoint.
    /// </summary>
    /// <returns>The created segment.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public JsonResult GetPluginMetadata()
    {
        var json = new
        {
            version = Plugin.Instance!.Version.ToString(3),
        };

        return new JsonResult(json);
    }

    /// <summary>
    /// Create MediaSegment for itemId.
    /// </summary>
    /// <param name="itemId">The ItemId.</param>
    /// <param name="providerId">Provider of the Segment.</param>
    /// <param name="segment">MediaSegment data.</param>
    /// <returns>The created segment.</returns>
    [HttpPost("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QueryResult<MediaSegmentDto>>> CreateSegmentAsync(
        [FromRoute, Required] Guid itemId,
        [FromQuery, Required] string providerId,
        [FromBody, Required] MediaSegmentDto segment)
    {
        var item = Plugin.Instance!.GetItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var seg = new Segment(itemId, new TimeRange(TimeSpan.FromTicks(segment.StartTicks).TotalSeconds, TimeSpan.FromTicks(segment.EndTicks).TotalSeconds));
        var mode = Plugin.MapSegmentTypeToMode(segment.Type);

        await Plugin.Instance!.UpdateTimestampAsync(seg, mode).ConfigureAwait(false);

        var queuedItem = new QueuedEpisode { EpisodeId = item.Id };

        await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync([queuedItem], CancellationToken.None).ConfigureAwait(false);

        return Ok();
    }

    /// <summary>
    /// Delete MediaSgment by segment id.
    /// </summary>
    /// <param name="segmentId">The Id of the media segment to delete.</param>
    /// <param name="itemId">The item id the segment belongs to (used to remove plugin DB entry).</param>
    /// <param name="type">The media segment type name (Intro/Recap/Preview/Outro).</param>
    /// <returns>HTTP 200 on success, 404 when item not found.</returns>
    [HttpDelete("{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> DeleteSegmentAsync(
        [FromRoute, Required] Guid segmentId,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string type)
    {
        // Delete the segment from Jellyfin's media segment manager
        await _mediaSegmentUpdateManager.DeleteSegmentAsync(segmentId).ConfigureAwait(false);

        AnalysisMode mode = type.ToLowerInvariant() switch
        {
            "intro" => AnalysisMode.Introduction,
            "recap" => AnalysisMode.Recap,
            "preview" => AnalysisMode.Preview,
            "outro" or "credits" => AnalysisMode.Credits,
            _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown segment type '{type}'")
        };

        await Plugin.Instance!.DeleteTimestampAsync(itemId, mode).ConfigureAwait(false);

        return Ok();
    }
}
