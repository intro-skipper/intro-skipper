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
using IntroSkipper.Manager;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

/// <summary>
/// Skip intro controller.
/// </summary>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public class SkipIntroController(MediaSegmentUpdateManager mediaSegmentUpdateManager) : ControllerBase
{
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
        var intro = GetIntro(id, mode);

        if (intro is null || !intro.Valid)
        {
            return NotFound();
        }

        return intro;
    }

    /// <summary>
    /// Updates the timestamps for the provided episode.
    /// </summary>
    /// <param name="id">Episode ID to update timestamps for.</param>
    /// <param name="timestamps">New timestamps Introduction/Credits start and end times.</param>
    /// <response code="204">New timestamps saved.</response>
    /// <response code="404">Given ID is not an Episode.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Episode/{Id}/Timestamps")]
    public async Task<ActionResult> UpdateTimestampsAsync([FromRoute] Guid id, [FromBody] TimeStamps timestamps)
    {
        // only update existing episodes
        var rawItem = Plugin.Instance!.GetItem(id);
        if (rawItem == null || rawItem is not Episode and not Movie)
        {
            return NotFound();
        }

        if (timestamps?.Introduction.End > 0.0)
        {
            var tr = new TimeRange(timestamps.Introduction.Start, timestamps.Introduction.End);
            Plugin.Instance!.Intros[id] = new Segment(id, tr);
        }

        if (timestamps?.Credits.End > 0.0)
        {
            var cr = new TimeRange(timestamps.Credits.Start, timestamps.Credits.End);
            Plugin.Instance!.Credits[id] = new Segment(id, cr);
        }

        Plugin.Instance!.SaveTimestamps(AnalysisMode.Introduction);
        Plugin.Instance!.SaveTimestamps(AnalysisMode.Credits);

        if (Plugin.Instance.Configuration.UpdateMediaSegments)
        {
            var seasonId = rawItem is Episode e ? e.SeasonId : rawItem.Id;
            var episode = Plugin.Instance!.QueuedMediaItems
                .FirstOrDefault(kvp => kvp.Key == seasonId).Value
                .FirstOrDefault(e => e.EpisodeId == rawItem.Id);

            if (episode is not null)
            {
                using var ct = new CancellationTokenSource();
                await mediaSegmentUpdateManager.UpdateMediaSegmentsAsync([episode], ct.Token).ConfigureAwait(false);
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
        if (rawItem == null || rawItem is not Episode and not Movie)
        {
            return NotFound();
        }

        var times = new TimeStamps();
        if (Plugin.Instance!.Intros.TryGetValue(id, out var introValue))
        {
            times.Introduction = introValue;
        }

        if (Plugin.Instance!.Credits.TryGetValue(id, out var creditValue))
        {
            times.Credits = creditValue;
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

        if (GetIntro(id, AnalysisMode.Introduction) is Intro intro)
        {
            segments[AnalysisMode.Introduction] = intro;
        }

        if (GetIntro(id, AnalysisMode.Credits) is Intro credits)
        {
            segments[AnalysisMode.Credits] = credits;
        }

        return segments;
    }

    /// <summary>Lookup and return the skippable timestamps for the provided item.</summary>
    /// <param name="id">Unique identifier of this episode.</param>
    /// <param name="mode">Mode.</param>
    /// <returns>Intro object if the provided item has an intro, null otherwise.</returns>
    private static Intro? GetIntro(Guid id, AnalysisMode mode)
    {
        try
        {
            var timestamp = Plugin.GetIntroByMode(id, mode);

            // Operate on a copy to avoid mutating the original Intro object stored in the dictionary.
            var segment = new Intro(timestamp);

            var config = Plugin.Instance!.Configuration;
            segment.IntroEnd = mode == AnalysisMode.Credits
                ? GetAdjustedIntroEnd(id, segment.IntroEnd, config)
                : segment.IntroEnd - config.RemainingSecondsOfIntro;

            if (config.PersistSkipButton)
            {
                segment.ShowSkipPromptAt = segment.IntroStart;
                segment.HideSkipPromptAt = segment.IntroEnd - 3;
            }
            else
            {
                segment.ShowSkipPromptAt = Math.Max(0, segment.IntroStart - config.ShowPromptAdjustment);
                segment.HideSkipPromptAt = Math.Min(
                    segment.IntroStart + config.HidePromptAdjustment,
                    segment.IntroEnd - 3);
            }

            return segment;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
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
    public ActionResult ResetIntroTimestamps([FromQuery] AnalysisMode mode, [FromQuery] bool eraseCache = false)
    {
        if (mode == AnalysisMode.Introduction)
        {
            Plugin.Instance!.Intros.Clear();
        }
        else if (mode == AnalysisMode.Credits)
        {
            Plugin.Instance!.Credits.Clear();
        }

        if (eraseCache)
        {
            FFmpegWrapper.DeleteCacheFiles(mode);
        }

        Plugin.Instance!.EpisodeStates.Clear();
        Plugin.Instance!.SaveTimestamps(mode);
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
