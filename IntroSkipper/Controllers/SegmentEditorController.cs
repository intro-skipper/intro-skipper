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
using MediaBrowser.Controller.Entities;
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

        var seg = new Segment(itemId, new TimeRange(TimeSpan.FromTicks(segment.StartTicks).TotalSeconds, TimeSpan.FromTicks(segment.EndTicks).TotalSeconds));
        var mode = AnalysisHelpers.MapSegmentTypeToMode(segment.Type);

        await _database.UpdateTimestampAsync(seg, mode, isUserProvided: true, cancellationToken: cancellationToken).ConfigureAwait(false);

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
    /// <returns>
    /// HTTP 200 on success, 400 when the requested type does not match the Jellyfin segment,
    /// or 404 when the commercial segment is not found.
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
        AnalysisMode requestedMode = type.ToLowerInvariant() switch
        {
            "intro" => AnalysisMode.Introduction,
            "recap" => AnalysisMode.Recap,
            "preview" => AnalysisMode.Preview,
            "outro" or "credits" => AnalysisMode.Credits,
            "commercial" => AnalysisMode.Commercial,
            _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown segment type '{type}'")
        };

        var existingSegment = await _mediaSegmentEditorService
            .GetSegmentAsync(itemId, segmentId, cancellationToken)
            .ConfigureAwait(false);

        var mode = requestedMode;
        if (existingSegment is not null)
        {
            mode = AnalysisHelpers.MapSegmentTypeToMode(existingSegment.Type);
            if (mode != requestedMode)
            {
                return BadRequest($"Segment '{segmentId}' is {existingSegment.Type}, not requested type '{type}'.");
            }
        }

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

        // Non-commercial modes have exactly one row per item and mode, so delete that
        // unambiguous counterpart even if Jellyfin's reported range has drifted. Commercial
        // rows still require the facade's exact epsilon match.
        var deleteSegment = mode == AnalysisMode.Commercial ? dbSegment : null;
        var deletedRows = await _database.DeleteTimestampAsync(itemId, mode, deleteSegment, cancellationToken).ConfigureAwait(false);

        try
        {
            await _mediaSegmentEditorService.DeleteSegmentAsync(segmentId).ConfigureAwait(false);
        }
        catch
        {
            // Jellyfin delete failed — restore the deleted plugin DB rows (including their
            // user-provided flag and config hash, so the restored rows are not treated as
            // stale) to avoid an orphaned Jellyfin segment. When the plugin DB had no matching
            // row, fall back to the Jellyfin-reported range so the still-existing Jellyfin
            // segment keeps a plugin-side counterpart. Rollback is deliberately uncancelable
            // once the plugin delete has completed.
            if (deletedRows.Count > 0)
            {
                foreach (var row in deletedRows)
                {
                    await _database.UpdateTimestampAsync(row.ToSegment(), mode, isUserProvided: row.IsUserProvided, configHash: row.ConfigHash, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
            }
            else if (dbSegment is not null)
            {
                await _database.UpdateTimestampAsync(dbSegment, mode, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        // Jellyfin delete succeeded — remove the episode from the season's analyzed-state list so
        // that the episode returns to NotAnalyzed and can be re-processed by the next analysis run.
        var deletedItem = Plugin.Instance!.GetItem(itemId);
        if (deletedItem is not null)
        {
            await _database.RemoveEpisodeIdAsync(ResolveSeasonStateKey(deletedItem), mode, itemId, cancellationToken).ConfigureAwait(false);
        }

        return Ok();
    }

    /// <summary>
    /// Resolves the season-state key for an item. Season states are keyed by the analysis
    /// queue's season key, which differs from the item's own SeasonId for in-season specials
    /// (grouped with the season they air within) and for episodes whose SeasonId could not be
    /// resolved. Prefer the queue key when the item is present in the cached queue.
    /// </summary>
    /// <param name="item">The item whose season-state key to resolve.</param>
    /// <returns>The season-state key.</returns>
    private static Guid ResolveSeasonStateKey(BaseItem item)
    {
        var queue = Plugin.Instance!.QueuedMediaItems;

        // Nearly every episode is queued under its own season, so check that bucket
        // before falling back to a scan of the whole queue for in-season specials
        // grouped under another season's key.
        if (item is Episode episode
            && queue.TryGetValue(episode.SeasonId, out var seasonEpisodes)
            && seasonEpisodes.Any(e => e.EpisodeId == item.Id))
        {
            return episode.SeasonId;
        }

        foreach (var (seasonId, episodes) in queue)
        {
            if (episodes.Any(e => e.EpisodeId == item.Id))
            {
                return seasonId;
            }
        }

        return item is Episode fallbackEpisode ? fallbackEpisode.SeasonId : item.Id;
    }
}
