// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Extended API for MediaSegments Management.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SegmentEditorController"/> class.
/// </remarks>
/// <param name="mediaSegmentUpdateManager">MediaSegmentUpdateManager.</param>
/// <param name="logger">Logger.</param>
[Authorize(Policy = "RequiresElevation")]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(MediaSegmentUpdateManager mediaSegmentUpdateManager, ILogger<SegmentEditorController> logger) : ControllerBase
{
    // Ten seconds keeps rollback bounded while allowing slow IO to complete.
    private static readonly TimeSpan RollbackTimeout = TimeSpan.FromSeconds(10);
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;
    private readonly ILogger<SegmentEditorController> _logger = logger;

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

        var seg = CreateSegment(itemId, segment);
        var mode = Plugin.MapSegmentTypeToMode(segment.Type);

        await Plugin.Instance!.UpdateTimestampAsync(seg, mode, cancellationToken).ConfigureAwait(false);

        var queuedItem = new QueuedEpisode { EpisodeId = item.Id };

        await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync([queuedItem], cancellationToken).ConfigureAwait(false);

        return Ok();
    }

    /// <summary>
    /// Delete MediaSgment by segment id.
    /// </summary>
    /// <param name="segmentId">The Id of the media segment to delete.</param>
    /// <param name="itemId">The item id the segment belongs to (used to remove plugin DB entry).</param>
    /// <param name="type">The media segment type name (Intro/Recap/Preview/Outro).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HTTP 200 on success, 404 when item not found.</returns>
    [HttpDelete("{segmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> DeleteSegmentAsync(
        [FromRoute, Required] Guid segmentId,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string type,
        CancellationToken cancellationToken = default)
    {
        MediaSegmentType segmentType = type.ToLowerInvariant() switch
        {
            "intro" => MediaSegmentType.Intro,
            "recap" => MediaSegmentType.Recap,
            "preview" => MediaSegmentType.Preview,
            "outro" or "credits" => MediaSegmentType.Outro,
            "commercial" => MediaSegmentType.Commercial,
            _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown segment type '{type}'")
        };

        AnalysisMode mode = Plugin.MapSegmentTypeToMode(segmentType);

        var existingSegment = await _mediaSegmentUpdateManager
            .GetSegmentAsync(itemId, segmentId, segmentType, cancellationToken)
            .ConfigureAwait(false);

        if (existingSegment is null)
        {
            return NotFound();
        }

        var dbSegment = CreateSegment(itemId, existingSegment);

        await Plugin.Instance!.DeleteTimestampAsync(itemId, mode, dbSegment, cancellationToken).ConfigureAwait(false);

        try
        {
            // Delete the segment from Jellyfin's media segment manager
            await _mediaSegmentUpdateManager.DeleteSegmentAsync(segmentId).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Deletion canceled for Jellyfin segment {SegmentId} on item {ItemId}; attempting DB rollback.", segmentId, itemId);
            await AttemptRollbackAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete Jellyfin segment {SegmentId} for item {ItemId}; attempting DB rollback.", segmentId, itemId);
            await AttemptRollbackAsync().ConfigureAwait(false);

            throw;
        }

        return Ok();

        async Task AttemptRollbackAsync()
        {
            try
            {
                using var rollbackTokenSource = new CancellationTokenSource(RollbackTimeout);
                var restored = await Plugin.Instance!.TryRestoreTimestampAsync(dbSegment, mode, rollbackTokenSource.Token).ConfigureAwait(false);
                if (restored)
                {
                    _logger.LogInformation("Rolled back DB delete for segment {SegmentId} on item {ItemId}.", segmentId, itemId);
                }
                else
                {
                    _logger.LogWarning("Skipped rollback for segment {SegmentId} on item {ItemId} because a newer segment exists.", segmentId, itemId);
                }
            }
            catch (OperationCanceledException rollbackCanceled)
            {
                _logger.LogWarning(rollbackCanceled, "Rollback canceled for segment {SegmentId} on item {ItemId}.", segmentId, itemId);
            }
            catch (Exception rollbackException)
            {
                RethrowIfCritical(rollbackException);
                _logger.LogError(rollbackException, "Failed to rollback DB delete for segment {SegmentId} on item {ItemId}.", segmentId, itemId);
            }
        }
    }

    private static void RethrowIfCritical(Exception ex)
    {
        if (ex is OutOfMemoryException or ThreadAbortException or StackOverflowException)
        {
            ExceptionDispatchInfo.Capture(ex).Throw();
        }
    }

    private static Segment CreateSegment(Guid itemId, MediaSegmentDto segment)
    {
        var startSeconds = TimeSpan.FromTicks(segment.StartTicks).TotalSeconds;
        var endSeconds = TimeSpan.FromTicks(segment.EndTicks).TotalSeconds;
        return new Segment(itemId, new TimeRange(startSeconds, endSeconds));
    }
}
