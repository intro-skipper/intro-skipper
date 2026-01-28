// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntroSkipper.Controllers;

/// <summary>
/// Skip intro controller.
/// </summary>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public class SkipIntroController(ISegmentService segmentService) : ControllerBase
{
    private readonly ISegmentService _segmentService = segmentService;

    /// <summary>
    /// Updates the timestamps for the provided episode.
    /// </summary>
    /// <param name="id">Episode ID to update timestamps for.</param>
    /// <param name="timestamps">New timestamps Introduction/Credits start and end times.</param>
    /// <param name="cancellationToken">Cancellation Token.</param>
    /// <response code="204">New timestamps saved.</response>
    /// <response code="404">Given ID is not an Episode.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Episode/{Id}/Timestamps")]
    public async Task<ActionResult> UpdateTimestampsAsync([FromRoute] Guid id, [FromBody] TimeStamps timestamps, CancellationToken cancellationToken = default)
    {
        // only update existing episodes
        var rawItem = Plugin.Instance!.GetItem(id);
        if (rawItem is not Episode and not Movie)
        {
            return NotFound();
        }

        if (timestamps is null)
        {
            return NoContent();
        }

        // Get SeasonId from episode if available
        var seasonId = rawItem is Episode episode ? episode.SeasonId : Guid.Empty;

        var segmentTypes = new[]
        {
            (AnalysisMode.Introduction, timestamps.Introduction),
            (AnalysisMode.Credits, timestamps.Credits),
            (AnalysisMode.Recap, timestamps.Recap),
            (AnalysisMode.Preview, timestamps.Preview),
            (AnalysisMode.Commercial, timestamps.Commercial)
        };

        foreach (var (mode, segment) in segmentTypes)
        {
            if (segment.Valid)
            {
                segment.EpisodeId = id;
                segment.SeasonId = seasonId;
                await _segmentService.CreateSegmentAsync(segment, mode, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        // Segments are automatically synced via the outbox processor

        return NoContent();
    }

    /// <summary>
    /// Gets the timestamps for the provided episode.
    /// </summary>
    /// <param name="id">Episode ID.</param>
    /// <response code="200">Sucess.</response>
    /// <response code="404">Given ID is not an Episode.</response>
    /// <returns>Episode Timestamps.</returns>
    [HttpGet("Episode/{Id}/Timestamps")]
    [ActionName("UpdateTimestamps")]
    public async Task<ActionResult<TimeStamps>> GetTimestamps([FromRoute] Guid id)
    {
        // only get return content for episodes
        var rawItem = Plugin.Instance!.GetItem(id);
        if (rawItem is not Episode and not Movie)
        {
            return NotFound();
        }

        var times = new TimeStamps();
        var segments = await _segmentService.GetSegmentsDictionaryAsync(id, CancellationToken.None).ConfigureAwait(false);

        if (segments.TryGetValue(AnalysisMode.Introduction, out var introSegment))
        {
            times.Introduction = introSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Credits, out var creditSegment))
        {
            times.Credits = creditSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Recap, out var recapSegment))
        {
            times.Recap = recapSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Preview, out var previewSegment))
        {
            times.Preview = previewSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Commercial, out var commercialSegment))
        {
            times.Commercial = commercialSegment;
        }

        return times;
    }

    /// <summary>
    /// Gets a dictionary of all skippable segments.
    /// </summary>
    /// <param name="id">Media ID.</param>
    /// <response code="200">Skippable segments dictionary.</response>
    /// <returns>Dictionary of skippable segments.</returns>
    [HttpGet("Episode/{id}/IntroSkipperSegments")]
    public async Task<ActionResult<Dictionary<AnalysisMode, Segment>>> GetSkippableSegments([FromRoute] Guid id)
    {
        var segments = await _segmentService.GetSegmentsDictionaryAsync(id, CancellationToken.None).ConfigureAwait(false);
        var result = new Dictionary<AnalysisMode, Segment>();

        if (segments.TryGetValue(AnalysisMode.Introduction, out var introSegment))
        {
            result[AnalysisMode.Introduction] = introSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Credits, out var creditSegment))
        {
            result[AnalysisMode.Credits] = creditSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Recap, out var recapSegment))
        {
            result[AnalysisMode.Recap] = recapSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Preview, out var previewSegment))
        {
            result[AnalysisMode.Preview] = previewSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Commercial, out var commercialSegment))
        {
            result[AnalysisMode.Commercial] = commercialSegment;
        }

        return result;
    }

    /// <summary>
    /// Erases all previously discovered introduction timestamps.
    /// </summary>
    /// <param name="mode">Mode.</param>
    /// <param name="eraseCache">Erase cache.</param>
    /// <response code="204">Operation successful.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Intros/EraseTimestamps")]
    public async Task<ActionResult> ResetIntroTimestamps([FromQuery] AnalysisMode mode, [FromQuery] bool eraseCache = false)
    {
        using var db = new IntroSkipperDbContext(Plugin.Instance!.DbPath);
        var segments = await db.DbSegment
            .Where(s => s.Type == mode)
            .ToListAsync()
            .ConfigureAwait(false);

        db.DbSegment.RemoveRange(segments);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (eraseCache && mode is AnalysisMode.Introduction or AnalysisMode.Credits)
        {
            await Task.Run(() => FFmpegWrapper.DeleteCacheFiles(mode)).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Erases all segments and rebuilds the database.
    /// </summary>
    /// <response code="204">Database erased and rebuilt.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Intros/RebuildDatabase")]
    public ActionResult RebuildDatabase()
    {
        using var db = new IntroSkipperDbContext(Plugin.Instance!.DbPath);
        db.Database.EnsureDeleted();
        db.Database.Migrate();
        return NoContent();
    }
}
