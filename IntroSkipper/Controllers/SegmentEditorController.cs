// SPDX-FileCopyrightText: 2025-2026 AbandonedCart
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(MediaSegmentEditorService mediaSegmentEditorService) : ControllerBase
{
    // Maximum difference in seconds between two time values to be considered equal.
    private const double TimeComparisonToleranceSeconds = 0.01;

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

        var seg = new Segment(itemId, new TimeRange(TimeSpan.FromTicks(segment.StartTicks).TotalSeconds, TimeSpan.FromTicks(segment.EndTicks).TotalSeconds));
        var mode = Plugin.MapSegmentTypeToMode(segment.Type);

        var mediaCategory = item is Movie ? QueuedMediaCategory.Movie : QueuedMediaCategory.Episode;
        await Plugin.Instance!.UpdateTimestampAsync(seg, mode, isUserProvided: true, append: true, mediaCategory: mediaCategory, cancellationToken: cancellationToken).ConfigureAwait(false);

        await _mediaSegmentEditorService.CreateOrReplaceSegmentAsync(item, segment, cancellationToken).ConfigureAwait(false);

        return Ok();
    }

    /// <summary>
    /// Delete MediaSgment by segment id.
    /// </summary>
    /// <param name="segmentId">The Id of the media segment to delete.</param>
    /// <param name="itemId">The item id the segment belongs to (used to remove plugin DB entry).</param>
    /// <param name="type">The media segment type name (Intro/Recap/Preview/Outro).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTTP 200 on success, 404 when the commercial segment is not found.</returns>
    [HttpDelete("{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteSegmentAsync(
        [FromRoute, Required] Guid segmentId,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string type,
        CancellationToken cancellationToken = default)
    {
        var existingSegment = await _mediaSegmentEditorService
            .GetSegmentAsync(itemId, segmentId, cancellationToken)
            .ConfigureAwait(false);

        AnalysisMode mode = type.ToLowerInvariant() switch
        {
            "intro" => AnalysisMode.Introduction,
            "recap" => AnalysisMode.Recap,
            "preview" => AnalysisMode.Preview,
            "outro" or "credits" => AnalysisMode.Credits,
            "commercial" => AnalysisMode.Commercial,
            _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown segment type '{type}'")
        };

        Segment? dbSegment = null;
        if (existingSegment is not null)
        {
            var startSeconds = TimeSpan.FromTicks(existingSegment.StartTicks).TotalSeconds;
            var endSeconds = TimeSpan.FromTicks(existingSegment.EndTicks).TotalSeconds;
            dbSegment = new Segment(itemId, new TimeRange(startSeconds, endSeconds));
        }

        if (dbSegment is null && mode == AnalysisMode.Commercial)
        {
            return NotFound();
        }

        // Read IsUserProvided before deleting so we can restore it accurately on rollback.
        // Match on type AND start/end times so that when multiple segments of the same mode exist
        // (e.g. Commercial) we identify the exact one being deleted rather than an arbitrary first match.
        var wasUserProvided = false;
        var pluginSegments = await Plugin.GetSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        var matchingSegment = pluginSegments.FirstOrDefault(s =>
            s.Type == mode &&
            (dbSegment is null || (Math.Abs(s.Start - dbSegment.Start) < TimeComparisonToleranceSeconds && Math.Abs(s.End - dbSegment.End) < TimeComparisonToleranceSeconds)));
        if (matchingSegment is not null)
        {
            wasUserProvided = matchingSegment.IsUserProvided;
        }

        var deletedItem = Plugin.Instance!.GetItem(itemId);
        var mediaCategory = deletedItem is Movie ? QueuedMediaCategory.Movie : QueuedMediaCategory.Episode;

        // Delete from the plugin DB first so it is consistent even if the Jellyfin delete fails.
        await Plugin.DeleteTimestampAsync(itemId, mode, dbSegment, cancellationToken).ConfigureAwait(false);

        try
        {
            await _mediaSegmentEditorService.DeleteSegmentAsync(segmentId).ConfigureAwait(false);
        }
        catch
        {
            // Jellyfin delete failed — restore the plugin DB entry to avoid an orphaned Jellyfin segment.
            if (dbSegment is not null)
            {
                await Plugin.Instance!.UpdateTimestampAsync(
                    dbSegment,
                    mode,
                    isUserProvided: wasUserProvided,
                    append: true,
                    mediaCategory: mediaCategory,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        // Jellyfin delete succeeded — remove the episode from the season's analyzed-state list so
        // that the episode returns to NotAnalyzed and can be re-processed by the next analysis run.
        if (deletedItem is not null)
        {
            var seasonId = deletedItem is Episode ep ? ep.SeasonId : deletedItem.Id;
            await Plugin.RemoveEpisodeIdAsync(seasonId, mode, itemId, cancellationToken).ConfigureAwait(false);
        }

        return Ok();
    }
}
