// SPDX-FileCopyrightText: 2025-2026 AbandonedCart
// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2025-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

/// <summary>
/// Exposes elevated editor endpoints for Jellyfin media segments.
/// </summary>
/// <remarks>
/// Endpoints that replace or copy segments are authoritative and can remove rows owned by
/// other providers. They validate the complete request before mutating either segment store.
/// </remarks>
/// <param name="mediaSegmentEditorService">The service that coordinates cross-store segment mutations.</param>
/// <param name="cacheDatabase">The facade for detection-cache data.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(
    MediaSegmentEditorService mediaSegmentEditorService,
    IDetectionCacheDatabase cacheDatabase) : ControllerBase
{
    private readonly MediaSegmentEditorService _mediaSegmentEditorService = mediaSegmentEditorService;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;

    /// <summary>
    /// Plugin meta endpoint.
    /// </summary>
    /// <returns>Plugin version metadata.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Returns the Intro Skipper identity and version.")]
    public JsonResult GetPluginMetadata()
    {
        var json = new
        {
            version = Plugin.Instance!.Version.ToString(4),

            // Discriminator for clients that must distinguish this plugin from the legacy
            // MediaSegments API plugin, which serves the same route shapes.
            name = "intro-skipper",
        };

        return new JsonResult(json);
    }

    /// <summary>
    /// Gets all Jellyfin media segments for an item across every provider.
    /// </summary>
    /// <remarks>
    /// The response includes provider information and the user-provided flag for Intro
    /// Skipper-owned rows. Unlike Jellyfin's core MediaSegments endpoint, it is never
    /// filtered: <see cref="IntroSkipper.Filters.MediaSegmentsFilterConvention"/> attaches the
    /// premiere-intro response filter only to controllers declared in Jellyfin's own
    /// Jellyfin.Api assembly, so no plugin controller can receive it regardless of its type
    /// name or route.
    /// </remarks>
    /// <param name="itemId">The ID of the item whose segments are retrieved.</param>
    /// <param name="cancellationToken">The token that cancels the asynchronous read.</param>
    /// <returns>An annotated segment list ordered by start position.</returns>
    [HttpGet("{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Returns every provider's segments for the item.")]
    [ProducesResponseType(StatusCodes.Status404NotFound, Description = "The item does not exist.")]
    public async Task<ActionResult<IReadOnlyList<EditorSegmentDto>>> GetSegmentsAsync(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        if (Plugin.Instance!.GetItem(itemId) is null)
        {
            return NotFound();
        }

        var segments = await _mediaSegmentEditorService.GetEditorSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        return Ok(segments);
    }

    /// <summary>
    /// Replaces every editor-managed segment for an item with a supplied set.
    /// </summary>
    /// <remarks>
    /// The set is authoritative across editor-managed types and providers. An empty body
    /// deletes all managed segments. The endpoint validates every entry before writing and
    /// returns <see cref="StatusCodes.Status400BadRequest"/> for unsupported, duplicate,
    /// empty, or null segments. A Jellyfin write failure restores the plugin database.
    /// </remarks>
    /// <param name="itemId">The ID of the item whose segments are replaced.</param>
    /// <param name="segments">The authoritative full segment set, expressed in ticks.</param>
    /// <param name="cancellationToken">The token that cancels work before both stores commit.</param>
    /// <returns>The refreshed segment view, including generated IDs.</returns>
    [HttpPut("{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Returns the refreshed authoritative segment set.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Description = "The supplied segment set is invalid.")]
    [ProducesResponseType(StatusCodes.Status404NotFound, Description = "The item does not exist.")]
    public async Task<ActionResult<IReadOnlyList<EditorSegmentDto>>> ReplaceSegmentsAsync(
        [FromRoute, Required] Guid itemId,
        [FromBody, Required] IReadOnlyList<MediaSegmentDto> segments,
        CancellationToken cancellationToken = default)
    {
        var item = Plugin.Instance!.GetItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var (validationError, normalized) = MediaSegmentEditorService.ValidateSegmentSet(segments);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        try
        {
            await _mediaSegmentEditorService
                .ReplaceEditorSegmentsAsync(item, ResolveSeasonStateKey(item), normalized, AnalysisHelpers.EditorManagedModes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SegmentIdConflictException ex)
        {
            // A supplied id can collide with a row outside the replaced scope (another
            // item, or a type the replace does not cover); that is a client error, not a
            // server fault. The service already restored the plugin database.
            return BadRequest($"Segment id '{ex.SegmentId}' already identifies another segment.");
        }

        var refreshed = await _mediaSegmentEditorService.GetEditorSegmentsAsync(itemId, cancellationToken).ConfigureAwait(false);
        return Ok(refreshed);
    }

    /// <summary>
    /// Copies selected source segments to independent target items.
    /// </summary>
    /// <remarks>
    /// Each target replacement is authoritative for the copied types. The endpoint
    /// continues after a non-critical target failure and reports that target's error in
    /// the response; cancellation stops the entire operation.
    /// <para>
    /// The source is not excluded from the targets: listing it applies the shifted set back
    /// onto itself, which is how a plain "move every segment by N ticks" request is
    /// expressed. Like any other target it is rewritten authoritatively, so other providers'
    /// rows of the copied types are replaced by Intro Skipper-owned ones.
    /// </para>
    /// </remarks>
    /// <param name="request">The source, targets, optional types, and time shift.</param>
    /// <param name="cancellationToken">The token that cancels the copy operation.</param>
    /// <returns>A per-target copy result.</returns>
    [HttpPost("Copy")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Returns the outcome for each target item.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Description = "The copy request or shifted segment ranges are invalid.")]
    [ProducesResponseType(StatusCodes.Status404NotFound, Description = "The source item does not exist.")]
    public async Task<ActionResult<CopySegmentsResponse>> CopySegmentsAsync(
        [FromBody, Required] CopySegmentsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Plugin.Instance!.GetItem(request.SourceItemId) is null)
        {
            return NotFound();
        }

        if (request.TargetItemIds is null || request.TargetItemIds.Count == 0)
        {
            return BadRequest("At least one target item is required.");
        }

        HashSet<MediaSegmentType>? requestedTypes = null;
        if (request.Types is not null)
        {
            if (request.Types.Count == 0)
            {
                return BadRequest("At least one segment type is required.");
            }

            requestedTypes = [];
            foreach (var type in request.Types)
            {
                if (!AnalysisHelpers.TryMapSegmentTypeToMode(type, out _))
                {
                    return BadRequest($"Segment type '{type}' is not supported by the editor.");
                }

                requestedTypes.Add(type);
            }
        }

        var (buildError, copiedSegments, copyModes) = await _mediaSegmentEditorService
            .BuildCopySegmentsAsync(request.SourceItemId, requestedTypes, request.TimeShiftTicks, cancellationToken)
            .ConfigureAwait(false);

        if (buildError is not null)
        {
            return BadRequest(buildError);
        }

        var targetItemIds = request.TargetItemIds.Distinct().ToList();
        var results = new List<CopyItemResult>(targetItemIds.Count);
        foreach (var targetItemId in targetItemIds)
        {
            var target = Plugin.Instance!.GetItem(targetItemId);
            if (target is null)
            {
                results.Add(new CopyItemResult(targetItemId, false, "Item not found."));
                continue;
            }

            try
            {
                // The shared segment list is safe to reuse per target: nothing on the
                // write path reads MediaSegmentDto.ItemId (both service and store key on
                // the target item id parameter).
                await _mediaSegmentEditorService
                    .ReplaceEditorSegmentsAsync(target, ResolveSeasonStateKey(target), copiedSegments, copyModes, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(new CopyItemResult(targetItemId, true, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!ex.IsCritical())
            {
                // A failed target's plugin database has already been restored by the
                // editor service's compensation before the exception surfaced.
                results.Add(new CopyItemResult(targetItemId, false, ex.Message));
            }
        }

        return Ok(new CopySegmentsResponse(results));
    }

    /// <summary>
    /// Gets cached boundary-snapping data for an item.
    /// </summary>
    /// <remarks>
    /// Positions are absolute seconds. This is a best-effort cache lookup: arrays are empty
    /// when no analysis data is cached, and black intervals are omitted when their scan
    /// anchor cannot be recovered.
    /// </remarks>
    /// <param name="itemId">The ID of the item whose snapping data is retrieved.</param>
    /// <param name="cancellationToken">The token that cancels the asynchronous cache read.</param>
    /// <returns>The snapping data.</returns>
    [HttpGet("{itemId:guid}/SnapPoints")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Returns cached boundary-snapping data, or empty arrays when unavailable.")]
    [ProducesResponseType(StatusCodes.Status404NotFound, Description = "The item does not exist.")]
    public async Task<ActionResult<SnapPointsResponse>> GetSnapPointsAsync(
        [FromRoute, Required] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        if (Plugin.Instance!.GetItem(itemId) is null)
        {
            return NotFound();
        }

        return Ok(await SnapPointAssembler.BuildAsync(itemId, _cacheDatabase, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists Jellyfin media segment rows whose items no longer exist in the library,
    /// grouped per item and split by owning provider.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The orphaned items.</returns>
    [HttpGet("Orphans")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Returns orphaned Jellyfin segment rows grouped by item and provider.")]
    public async Task<ActionResult<IReadOnlyList<ItemSegmentCounts>>> GetOrphanedSegmentsAsync(CancellationToken cancellationToken = default)
    {
        var orphans = await _mediaSegmentEditorService.GetOrphanedSegmentsAsync(ItemExists, cancellationToken).ConfigureAwait(false);
        return Ok(orphans);
    }

    /// <summary>
    /// Deletes Intro Skipper's Jellyfin segment rows for items that no longer exist in the
    /// library. The orphan set is recomputed server-side; other providers' rows are kept.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of cleaned-up items.</returns>
    [HttpDelete("Orphans")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Returns the number of orphaned items cleaned up.")]
    public async Task<ActionResult<DeleteOrphansResponse>> DeleteOrphanedSegmentsAsync(CancellationToken cancellationToken = default)
    {
        var deletedItemCount = await _mediaSegmentEditorService.DeleteOrphanedSegmentsAsync(ItemExists, cancellationToken).ConfigureAwait(false);
        return Ok(new DeleteOrphansResponse(deletedItemCount));
    }

    /// <summary>
    /// Create MediaSegment for itemId.
    /// </summary>
    /// <remarks>
    /// The write is applied to the plugin database first and then to Jellyfin; a Jellyfin
    /// write failure restores the plugin database.
    /// </remarks>
    /// <param name="itemId">The ItemId.</param>
    /// <param name="providerId">
    /// Accepted for compatibility with clients written against Jellyfin's segment API and
    /// ignored: a segment created here is always attributed to Intro Skipper.
    /// </param>
    /// <param name="segment">MediaSegment data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTTP 200 with an empty body on success.</returns>
    [HttpPost("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Creates or replaces the requested media segment.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Description = "The supplied segment range or type is invalid, or its id already identifies another segment.")]
    [ProducesResponseType(StatusCodes.Status404NotFound, Description = "The item does not exist.")]
    public async Task<ActionResult> CreateSegmentAsync(
        [FromRoute, Required] Guid itemId,
        [FromQuery] string? providerId,
        [FromBody, Required] MediaSegmentDto segment,
        CancellationToken cancellationToken = default)
    {
        var item = Plugin.Instance!.GetItem(itemId);
        if (item is null)
        {
            return NotFound();
        }

        // Reject invalid input as a client error before either store commits. The set
        // validator applies the same per-segment rules the replace endpoint enforces; its
        // cross-segment rules cannot fire on a single entry, so the two endpoints reject
        // the same input with the same message and cannot drift apart.
        var (validationError, _) = MediaSegmentEditorService.ValidateSegmentSet([segment]);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        try
        {
            await _mediaSegmentEditorService.CreateOrReplaceSegmentAsync(item, ResolveSeasonStateKey(item), segment, cancellationToken).ConfigureAwait(false);
        }
        catch (SegmentIdConflictException ex)
        {
            // The service already restored the plugin database before rethrowing.
            return BadRequest($"Segment id '{ex.SegmentId}' already identifies another segment.");
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
    /// HTTP 200 on success, 400 when <paramref name="itemId"/> is empty or <paramref name="type"/>
    /// names no editor-managed type or does not match the Jellyfin segment, or 404 when the
    /// segment exists in neither store. A segment id owned by a different item leaves Jellyfin
    /// untouched while the item's own plugin row is still removed. Deleting another provider's
    /// segment removes only that Jellyfin row; Intro Skipper's own rows and season state stay
    /// untouched.
    /// </returns>
    [HttpDelete("{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Deletes the requested segment from both stores.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Description = "The item id is empty, or the requested type is unknown or does not match the Jellyfin segment.")]
    [ProducesResponseType(StatusCodes.Status404NotFound, Description = "The requested segment does not exist.")]
    public async Task<ActionResult> DeleteSegmentAsync(
        [FromRoute, Required] Guid segmentId,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string type,
        CancellationToken cancellationToken = default)
    {
        // [Required] never rejects a non-nullable value type, so an omitted itemId binds to
        // Guid.Empty and would target the empty-guid rows that Jellyfin's table really can
        // hold (see DeleteOrphanedSegmentsAsync, which excludes them for the same reason).
        if (itemId.Equals(Guid.Empty))
        {
            return BadRequest("A non-empty itemId is required.");
        }

        AnalysisMode? mappedMode = type.ToLowerInvariant() switch
        {
            "intro" => AnalysisMode.Introduction,
            "recap" => AnalysisMode.Recap,
            "preview" => AnalysisMode.Preview,
            "outro" or "credits" => AnalysisMode.Credits,
            "commercial" => AnalysisMode.Commercial,
            _ => null,
        };

        if (mappedMode is not AnalysisMode requestedMode)
        {
            return BadRequest($"Unknown segment type '{type}'.");
        }

        // An item that has left the library has no season entry to clear, but its rows can
        // still be deleted, so the delete itself does not depend on the lookup succeeding.
        var item = Plugin.Instance!.GetItem(itemId);
        var seasonStateKey = item is null ? (Guid?)null : ResolveSeasonStateKey(item);

        var (deleted, actualType) = await _mediaSegmentEditorService
            .DeleteSegmentAsync(itemId, segmentId, requestedMode, seasonStateKey, cancellationToken)
            .ConfigureAwait(false);

        if (actualType is MediaSegmentType mismatchedType)
        {
            return BadRequest($"Segment '{segmentId}' is {mismatchedType}, not requested type '{type}'.");
        }

        return deleted ? Ok() : NotFound();
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
        // Episodes are nearly always queued under their own season and movies always under
        // their own id, so probing that key first keeps the queue scan for the in-season
        // specials that need it. An unqueued item falls back to the same key.
        var preferredKey = item is Episode episode ? episode.SeasonId : item.Id;
        Plugin.Instance!.FindQueuedItem(item.Id, preferredKey, out var seasonStateKey);
        return seasonStateKey;
    }

    private static bool ItemExists(Guid itemId) => Plugin.Instance!.GetItem(itemId) is not null;
}
