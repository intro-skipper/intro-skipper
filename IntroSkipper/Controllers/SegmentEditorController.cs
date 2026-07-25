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
using MediaBrowser.Model.Querying;
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
/// <param name="database">The facade for segment and season-state data.</param>
/// <param name="cacheDatabase">The facade for detection-cache data.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(
    MediaSegmentEditorService mediaSegmentEditorService,
    IIntroSkipperDatabase database,
    IDetectionCacheDatabase cacheDatabase) : ControllerBase
{
    private readonly MediaSegmentEditorService _mediaSegmentEditorService = mediaSegmentEditorService;
    private readonly IIntroSkipperDatabase _database = database;
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

        var validationError = ValidateSegmentSet(segments);
        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        var normalized = NormalizeForReplace(segments, itemId);

        await _mediaSegmentEditorService
            .ReplaceEditorSegmentsAsync(item, ResolveSeasonStateKey(item), normalized, AnalysisHelpers.EditorManagedModes, cancellationToken)
            .ConfigureAwait(false);

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

        var sourceView = await _mediaSegmentEditorService.GetEditorSegmentsAsync(request.SourceItemId, cancellationToken).ConfigureAwait(false);

        var copiedSegments = new List<MediaSegmentDto>();
        var copiedCommercialRanges = new List<(long Start, long End)>();
        foreach (var segment in SelectCopySources(sourceView, requestedTypes))
        {
            long startTicks;
            long endTicks;
            try
            {
                startTicks = checked(segment.StartTicks + request.TimeShiftTicks);
                endTicks = checked(segment.EndTicks + request.TimeShiftTicks);
            }
            catch (OverflowException)
            {
                return BadRequest($"Time shift overflows the tick range for a '{segment.Type}' segment.");
            }

            if (endTicks <= 0)
            {
                // Under replace semantics, dropping this segment would silently delete that
                // type on every target; reject the request instead.
                return BadRequest($"Time shift moves a '{segment.Type}' segment entirely before the item start.");
            }

            // A negative shift may push a segment's start before zero; clamp instead of
            // failing so a plain "shift everything earlier" request stays usable.
            var clampedStart = Math.Max(0, startTicks);

            if (endTicks <= clampedStart)
            {
                return BadRequest($"Time shift produces an empty '{segment.Type}' segment.");
            }

            if (segment.Type == MediaSegmentType.Commercial)
            {
                var range = (clampedStart, endTicks);
                if (copiedCommercialRanges.Contains(range))
                {
                    // Clamping can collapse distinct source commercials onto one exact range.
                    continue;
                }

                if (copiedCommercialRanges.Any(existing => AreCommercialRangesEquivalent(existing, range)))
                {
                    return BadRequest("Commercial segment ranges must differ by more than the comparison tolerance.");
                }

                copiedCommercialRanges.Add(range);
            }

            copiedSegments.Add(new MediaSegmentDto
            {
                Type = segment.Type,
                StartTicks = clampedStart,
                EndTicks = endTicks,
            });
        }

        if (copiedSegments.Count == 0)
        {
            // Applying an empty authoritative set would erase the requested types on every
            // target; an empty source selection is almost certainly a caller error.
            return BadRequest("The source item has no segments of the requested types.");
        }

        // Replace exactly the types being written. Requested types the source does not
        // carry are deliberately left untouched on the targets: the replace is
        // authoritative, so including them would delete target segments of those types,
        // including user edits and other providers' rows that re-analysis cannot restore.
        var copyModes = copiedSegments
            .Select(segment => AnalysisHelpers.MapSegmentTypeToMode(segment.Type))
            .Distinct()
            .ToList();

        var results = new List<CopyItemResult>(request.TargetItemIds.Count);
        foreach (var targetItemId in request.TargetItemIds.Distinct())
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
    public async Task<ActionResult<IReadOnlyList<OrphanedItemSegments>>> GetOrphanedSegmentsAsync(CancellationToken cancellationToken = default)
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
    /// <param name="itemId">The ItemId.</param>
    /// <param name="providerId">Provider of the Segment.</param>
    /// <param name="segment">MediaSegment data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created segment.</returns>
    [HttpPost("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Creates or replaces the requested media segment.")]
    [ProducesResponseType(StatusCodes.Status404NotFound, Description = "The item does not exist.")]
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
    /// <param name="itemId">The item id that owns the segment; scopes both the plugin DB row and the Jellyfin delete.</param>
    /// <param name="type">The media segment type name (Intro/Recap/Preview/Outro).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// HTTP 200 on success, 400 when the requested type does not match the Jellyfin segment,
    /// or 404 when the commercial segment is not found. A segment id owned by a different item
    /// leaves Jellyfin untouched while the item's own plugin row is still removed.
    /// </returns>
    [HttpDelete("{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK, Description = "Deletes the requested segment from both stores.")]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Description = "The requested type does not match the Jellyfin segment.")]
    [ProducesResponseType(StatusCodes.Status404NotFound, Description = "The requested commercial segment does not exist.")]
    public async Task<ActionResult> DeleteSegmentAsync(
        [FromRoute, Required] Guid segmentId,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string type,
        CancellationToken cancellationToken = default)
    {
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

        var (deleted, actualType) = await _mediaSegmentEditorService
            .DeleteSegmentAsync(itemId, segmentId, requestedMode, cancellationToken)
            .ConfigureAwait(false);

        if (actualType is MediaSegmentType mismatchedType)
        {
            return BadRequest($"Segment '{segmentId}' is {mismatchedType}, not requested type '{type}'.");
        }

        if (!deleted)
        {
            return NotFound();
        }

        // Both segment stores have committed. Complete derived season bookkeeping even if
        // the request is canceled now so the next analysis can re-process the episode.
        var deletedItem = Plugin.Instance!.GetItem(itemId);
        if (deletedItem is not null)
        {
            await _database
                .RemoveEpisodeIdAsync(ResolveSeasonStateKey(deletedItem), requestedMode, itemId, CancellationToken.None)
                .ConfigureAwait(false);
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

    private static string? ValidateSegmentSet(IReadOnlyList<MediaSegmentDto> segments)
    {
        var seenNonCommercialModes = new HashSet<AnalysisMode>();
        var seenCommercialRanges = new List<(long Start, long End)>();
        var seenIds = new HashSet<Guid>();
        foreach (var segment in segments)
        {
            if (segment is null)
            {
                return "Segment entries must not be null.";
            }

            // A supplied id becomes the Jellyfin row's primary key, so a repeat would fail
            // the insert mid-transaction and surface as a server error rather than a client
            // one. The empty guid is exempt: it is how callers ask for a generated id, and
            // every new segment carries it.
            if (segment.Id != Guid.Empty && !seenIds.Add(segment.Id))
            {
                return $"Segment id '{segment.Id}' appears more than once.";
            }

            if (!AnalysisHelpers.TryMapSegmentTypeToMode(segment.Type, out var mode))
            {
                return $"Segment type '{segment.Type}' is not supported by the editor.";
            }

            if (segment.StartTicks < 0)
            {
                return $"Segment start must not be negative (type '{segment.Type}').";
            }

            if (segment.EndTicks <= segment.StartTicks)
            {
                return $"Segment end must be after its start (type '{segment.Type}').";
            }

            if (mode == AnalysisMode.Commercial)
            {
                var range = (segment.StartTicks, segment.EndTicks);
                if (seenCommercialRanges.Contains(range))
                {
                    // Exact duplicates are normalized away before writing.
                    continue;
                }

                if (seenCommercialRanges.Any(existing => AreCommercialRangesEquivalent(existing, range)))
                {
                    return "Commercial segment ranges must differ by more than the comparison tolerance.";
                }

                seenCommercialRanges.Add(range);
                continue;
            }

            if (!seenNonCommercialModes.Add(mode))
            {
                return $"Only one segment of type '{segment.Type}' is allowed per item.";
            }
        }

        return null;
    }

    private static bool AreCommercialRangesEquivalent(
        (long Start, long End) first,
        (long Start, long End) second)
        => Math.Abs(TimeSpan.FromTicks(first.Start).TotalSeconds - TimeSpan.FromTicks(second.Start).TotalSeconds)
                <= IntroSkipperDatabase.SegmentComparisonEpsilon
            && Math.Abs(TimeSpan.FromTicks(first.End).TotalSeconds - TimeSpan.FromTicks(second.End).TotalSeconds)
                <= IntroSkipperDatabase.SegmentComparisonEpsilon;

    private static List<MediaSegmentDto> NormalizeForReplace(IReadOnlyList<MediaSegmentDto> segments, Guid itemId)
    {
        var normalized = new List<MediaSegmentDto>(segments.Count);
        var seenCommercialRanges = new HashSet<(long Start, long End)>();
        foreach (var segment in segments)
        {
            // Identical commercial entries are silently deduplicated, mirroring the
            // create endpoint's identical-entry semantics.
            if (segment.Type == MediaSegmentType.Commercial
                && !seenCommercialRanges.Add((segment.StartTicks, segment.EndTicks)))
            {
                continue;
            }

            segment.ItemId = itemId;
            normalized.Add(segment);
        }

        return normalized;
    }

    /// <summary>
    /// Selects source segments that can be safely applied as an authoritative replacement.
    /// </summary>
    /// <remarks>
    /// Jellyfin permits several providers to hold a segment of one type, but the plugin
    /// permits only one non-commercial segment per type. The result therefore contains one
    /// non-commercial segment per type: Intro Skipper's row wins, then the earliest start.
    /// This prevents a replacement set that violates the plugin's unique index.
    /// </remarks>
    /// <param name="sourceView">The source item's cross-provider segment view.</param>
    /// <param name="requestedTypes">The types to copy, or <see langword="null"/> for every editor-managed type present on the source.</param>
    /// <returns>The selected segments ordered by start position.</returns>
    private static List<EditorSegmentDto> SelectCopySources(
        IReadOnlyList<EditorSegmentDto> sourceView,
        HashSet<MediaSegmentType>? requestedTypes)
    {
        var candidates = sourceView
            .Where(segment => AnalysisHelpers.TryMapSegmentTypeToMode(segment.Type, out _))
            .Where(segment => requestedTypes is null || requestedTypes.Contains(segment.Type));

        var selected = new List<EditorSegmentDto>();
        foreach (var group in candidates.GroupBy(segment => segment.Type))
        {
            if (group.Key == MediaSegmentType.Commercial)
            {
                selected.AddRange(group);
                continue;
            }

            selected.Add(group
                .OrderByDescending(segment => string.Equals(segment.ProviderId, JellyfinSegmentStore.ProviderId, StringComparison.Ordinal))
                .ThenBy(segment => segment.StartTicks)
                .First());
        }

        return selected.OrderBy(segment => segment.StartTicks).ToList();
    }

    private static bool ItemExists(Guid itemId) => Plugin.Instance!.GetItem(itemId) is not null;
}
