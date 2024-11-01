// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
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
public class SkipIntroController(MediaSegmentUpdateManager mediaSegmentUpdateManager) : ControllerBase
{
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;

    /// <summary>
    /// Returns the timestamps of the introduction in a television episode. Responses are in API version 1 format.
    /// </summary>
    /// <param name="id">ID of the episode. Required.</param>
    /// <param name="mode">Timestamps to return. Optional. Defaults to Introduction for backwards compatibility.</param>
    /// <response code="200">Episode contains an intro.</response>
    /// <response code="404">Failed to find an intro in the provided episode.</response>
    /// <returns>Detected intro.</returns>
    [HttpGet("Episode/{id}/IntroTimestamps")]
    [HttpGet("Episode/{id}/IntroTimestamps/v1")]
    public ActionResult<Intro> GetIntroTimestamps(
        [FromRoute] Guid id,
        [FromQuery] AnalysisMode mode = AnalysisMode.Introduction)
    {
        var intro = Plugin.Instance!.GetSegmentByMode(id, mode);

        if (!intro.Valid)
        {
            return NotFound();
        }

        return new Intro(intro);
    }

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

        if (timestamps?.Introduction.End > 0.0)
        {
            var tr = new TimeRange(timestamps.Introduction.Start, timestamps.Introduction.End);
            await Plugin.Instance!.UpdateTimestamps(new Dictionary<Guid, Segment> { [id] = new Segment(id, tr) }, AnalysisMode.Introduction).ConfigureAwait(false);
        }

        if (timestamps?.Credits.End > 0.0)
        {
            var tr = new TimeRange(timestamps.Credits.Start, timestamps.Credits.End);
            await Plugin.Instance!.UpdateTimestamps(new Dictionary<Guid, Segment> { [id] = new Segment(id, tr) }, AnalysisMode.Credits).ConfigureAwait(false);
        }

        if (Plugin.Instance.Configuration.UpdateMediaSegments)
        {
            var episode = Plugin.Instance!.QueuedMediaItems[rawItem is Episode e ? e.SeasonId : rawItem.Id]
                .FirstOrDefault(q => q.EpisodeId == rawItem.Id);

            if (episode is not null)
            {
                await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync([episode], cancellationToken).ConfigureAwait(false);
            }
        }

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
    public ActionResult<TimeStamps> GetTimestamps([FromRoute] Guid id)
    {
        // only get return content for episodes
        var rawItem = Plugin.Instance!.GetItem(id);
        if (rawItem is not Episode and not Movie)
        {
            return NotFound();
        }

        var times = new TimeStamps();
        var segments = Plugin.Instance!.GetSegmentsById(id);

        if (segments.TryGetValue(AnalysisMode.Introduction, out var introSegment))
        {
            times.Introduction = introSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Credits, out var creditSegment))
        {
            times.Credits = creditSegment;
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
    public ActionResult<Dictionary<AnalysisMode, Intro>> GetSkippableSegments([FromRoute] Guid id)
    {
        var segments = new Dictionary<AnalysisMode, Intro>();
        var dbSegments = Plugin.Instance!.GetSegmentsById(id);

        if (dbSegments.TryGetValue(AnalysisMode.Introduction, out var introSegment))
        {
            segments[AnalysisMode.Introduction] = new Intro(introSegment);
        }

        if (dbSegments.TryGetValue(AnalysisMode.Introduction, out var creditSegment))
        {
            segments[AnalysisMode.Credits] = new Intro(creditSegment);
        }

        return segments;
    }

    private static double GetAdjustedIntroEnd(Guid id, double segmentEnd, PluginConfiguration config)
    {
        var runTime = TimeSpan.FromTicks(Plugin.Instance!.GetItem(id)?.RunTimeTicks ?? 0).TotalSeconds;
        return runTime > 0 && runTime < segmentEnd + 1
            ? runTime
            : segmentEnd - config.RemainingSecondsOfIntro;
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

        if (eraseCache)
        {
            FFmpegWrapper.DeleteCacheFiles(mode);
        }

        return NoContent();
    }

    /// <summary>
    /// Gets the user interface configuration.
    /// </summary>
    /// <response code="200">UserInterfaceConfiguration returned.</response>
    /// <returns>UserInterfaceConfiguration.</returns>
    [HttpGet]
    [Route("Intros/UserInterfaceConfiguration")]
    public ActionResult<UserInterfaceConfiguration> GetUserInterfaceConfiguration()
    {
        var config = Plugin.Instance!.Configuration;
        return new UserInterfaceConfiguration(
            config.SkipButtonEnabled,
            config.SkipButtonIntroText,
            config.SkipButtonEndCreditsText,
            config.AutoSkip,
            config.AutoSkipCredits,
            config.ClientList);
    }
}
