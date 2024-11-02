// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Manager;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Audio fingerprint visualization controller. Allows browsing fingerprints on a per episode basis.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="VisualizationController"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="mediaSegmentUpdateManager">Media Segment Update Manager.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Intros")]
public class VisualizationController(ILogger<VisualizationController> logger, MediaSegmentUpdateManager mediaSegmentUpdateManager) : ControllerBase
{
    private readonly ILogger<VisualizationController> _logger = logger;
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;

    /// <summary>
    /// Returns all show names and seasons.
    /// </summary>
    /// <returns>Dictionary of show names to a list of season names.</returns>
    [HttpGet("Shows")]
    public ActionResult<Dictionary<Guid, ShowInfos>> GetShowSeasons()
    {
        _logger.LogDebug("Returning season IDs by series name");

        var showSeasons = new Dictionary<Guid, ShowInfos>();

        foreach (var kvp in Plugin.Instance!.QueuedMediaItems)
        {
            if (kvp.Value.FirstOrDefault() is QueuedEpisode first)
            {
                var seriesId = first.SeriesId;
                var seasonId = kvp.Key;

                var seasonNumber = first.SeasonNumber;
                if (!showSeasons.TryGetValue(seriesId, out var showInfo))
                {
                    showInfo = new ShowInfos { SeriesName = first.SeriesName, ProductionYear = GetProductionYear(seriesId), LibraryName = GetLibraryName(seriesId), IsMovie = first.IsMovie, Seasons = [] };
                    showSeasons[seriesId] = showInfo;
                }

                showInfo.Seasons[seasonId] = seasonNumber;
            }
        }

        // Sort the dictionary by SeriesName and the seasons by SeasonName
        var sortedShowSeasons = showSeasons
            .OrderBy(kvp => kvp.Value.SeriesName)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => new ShowInfos
                {
                    SeriesName = kvp.Value.SeriesName,
                    ProductionYear = kvp.Value.ProductionYear,
                    LibraryName = kvp.Value.LibraryName,
                    IsMovie = kvp.Value.IsMovie,
                    Seasons = kvp.Value.Seasons
                        .OrderBy(s => s.Value)
                        .ToDictionary(s => s.Key, s => s.Value)
                });

        return sortedShowSeasons;
    }

    /// <summary>
    /// Returns the analyzer actions for the provided season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("AnalyzerActions/{SeasonId}")]
    public ActionResult<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>> GetAnalyzerAction([FromRoute] Guid seasonId)
    {
        if (!Plugin.Instance!.QueuedMediaItems.ContainsKey(seasonId))
        {
            return NotFound();
        }

        return Ok(Plugin.Instance!.GetAnalyzerAction(seasonId));
    }

    /// <summary>
    /// Returns the names and unique identifiers of all episodes in the provided season.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("Show/{SeriesId}/{SeasonId}")]
    public ActionResult<List<EpisodeVisualization>> GetSeasonEpisodes([FromRoute] Guid seriesId, [FromRoute] Guid seasonId)
    {
        if (!Plugin.Instance!.QueuedMediaItems.TryGetValue(seasonId, out var episodes))
        {
            return NotFound();
        }

        if (!episodes.Any(e => e.SeriesId == seriesId))
        {
            return NotFound();
        }

        var showName = episodes.FirstOrDefault()?.SeriesName!;

        return episodes.Select(e => new EpisodeVisualization(e.EpisodeId, e.Name)).ToList();
    }

    /// <summary>
    /// Fingerprint the provided episode and returns the uncompressed fingerprint data points.
    /// </summary>
    /// <param name="id">Episode id.</param>
    /// <returns>Read only collection of fingerprint points.</returns>
    [HttpGet("Episode/{Id}/Chromaprint")]
    public ActionResult<uint[]> GetEpisodeFingerprint([FromRoute] Guid id)
    {
        // Search through all queued episodes to find the requested id
        foreach (var season in Plugin.Instance!.QueuedMediaItems)
        {
            foreach (var needle in season.Value)
            {
                if (needle.EpisodeId == id)
                {
                    return FFmpegWrapper.Fingerprint(needle, AnalysisMode.Introduction);
                }
            }
        }

        return NotFound();
    }

    /// <summary>
    /// Erases all timestamps for the provided season.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="eraseCache">Erase cache.</param>
    /// <param name="cancellationToken">Cancellation Token.</param>
    /// <response code="204">Season timestamps erased.</response>
    /// <response code="404">Unable to find season in provided series.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Show/{SeriesId}/{SeasonId}")]
    public async Task<ActionResult> EraseSeasonAsync([FromRoute] Guid seriesId, [FromRoute] Guid seasonId, [FromQuery] bool eraseCache = false, CancellationToken cancellationToken = default)
    {
        var episodes = Plugin.Instance!.QueuedMediaItems[seasonId];

        if (episodes.Count == 0)
        {
            return NotFound();
        }

        _logger.LogInformation("Erasing timestamps for series {SeriesId} season {SeasonId} at user request", seriesId, seasonId);

        try
        {
            using var db = new IntroSkipperDbContext(Plugin.Instance!.DbPath);

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segments = Plugin.Instance!.GetSegmentsById(episode.EpisodeId);

                if (segments.TryGetValue(AnalysisMode.Introduction, out var introSegment))
                {
                    db.DbSegment.Remove(new DbSegment(introSegment, AnalysisMode.Introduction));
                }

                if (segments.TryGetValue(AnalysisMode.Introduction, out var creditSegment))
                {
                    db.DbSegment.Remove(new DbSegment(creditSegment, AnalysisMode.Credits));
                }

                if (eraseCache)
                {
                    await Task.Run(() => FFmpegWrapper.DeleteEpisodeCache(episode.EpisodeId), cancellationToken).ConfigureAwait(false);
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (Plugin.Instance.Configuration.UpdateMediaSegments)
            {
                await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync(episodes, cancellationToken).ConfigureAwait(false);
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    /// <summary>
    /// Updates the analyzer actions for the provided season.
    /// </summary>
    /// <param name="request">Update analyzer actions request.</param>
    /// <returns>No content.</returns>
    [HttpPost("AnalyzerActions/UpdateSeason")]
    public async Task<ActionResult> UpdateAnalyzerActions([FromBody] UpdateAnalyzerActionsRequest request)
    {
        await Plugin.Instance!.UpdateAnalyzerActionAsync(request.Id, request.AnalyzerActions).ConfigureAwait(false);

        return NoContent();
    }

    private static string GetProductionYear(Guid seriesId)
    {
        return seriesId == Guid.Empty
            ? "Unknown"
            : Plugin.Instance?.GetItem(seriesId)?.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
    }

    private static string GetLibraryName(Guid seriesId)
    {
        if (seriesId == Guid.Empty)
        {
            return "Unknown";
        }

        var collectionFolders = Plugin.Instance?.GetCollectionFolders(seriesId);
        return collectionFolders?.Count > 0
            ? string.Join(", ", collectionFolders.Select(folder => folder.Name))
            : "Unknown";
    }
}
