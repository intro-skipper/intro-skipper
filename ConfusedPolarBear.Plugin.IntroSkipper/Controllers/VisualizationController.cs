using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Controllers;

/// <summary>
/// Audio fingerprint visualization controller. Allows browsing fingerprints on a per episode basis.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="VisualizationController"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Intros")]
public class VisualizationController(ILogger<VisualizationController> logger) : ControllerBase
{
    private readonly ILogger<VisualizationController> _logger = logger;

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
                    showInfo = new ShowInfos { SeriesName = first.SeriesName, ProductionYear = GetProductionYear(seriesId), LibraryName = GetLibraryName(seriesId), Seasons = [] };
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
                    Seasons = kvp.Value.Seasons
                        .OrderBy(s => s.Value)
                        .ToDictionary(s => s.Key, s => s.Value)
                });

        return sortedShowSeasons;
    }

    /// <summary>
    /// Returns the ignore list for the provided season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("IgnoreListSeason/{SeasonId}")]
    public ActionResult<IgnoreListItem> GetIgnoreListSeason([FromRoute] Guid seasonId)
    {
        if (!Plugin.Instance!.QueuedMediaItems.ContainsKey(seasonId))
        {
            return NotFound();
        }

        if (!Plugin.Instance!.IgnoreList.TryGetValue(seasonId, out _))
        {
            return new IgnoreListItem(seasonId);
        }

        return new IgnoreListItem(Plugin.Instance!.IgnoreList[seasonId]);
    }

    /// <summary>
    /// Returns the ignore list for the provided series.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("IgnoreListSeries/{SeriesId}")]
    public ActionResult<IgnoreListItem> GetIgnoreListSeries([FromRoute] Guid seriesId)
    {
        var seasonIds = Plugin.Instance!.QueuedMediaItems
            .Where(kvp => kvp.Value.Any(e => e.SeriesId == seriesId))
            .Select(kvp => kvp.Key)
            .ToList();

        if (seasonIds.Count == 0)
        {
            return NotFound();
        }

        return new IgnoreListItem(Guid.Empty)
        {
            IgnoreIntro = seasonIds.All(seasonId => Plugin.Instance!.IsIgnored(seasonId, AnalysisMode.Introduction)),
            IgnoreCredits = seasonIds.All(seasonId => Plugin.Instance!.IsIgnored(seasonId, AnalysisMode.Credits))
        };
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
    /// <response code="204">Season timestamps erased.</response>
    /// <response code="404">Unable to find season in provided series.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Show/{SeriesId}/{SeasonId}")]
    public ActionResult EraseSeason([FromRoute] Guid seriesId, [FromRoute] Guid seasonId, [FromQuery] bool eraseCache = false)
    {
        var episodes = Plugin.Instance!.QueuedMediaItems
            .Where(kvp => kvp.Key == seasonId)
            .SelectMany(kvp => kvp.Value.Where(e => e.SeriesId == seriesId))
            .ToList();

        if (episodes.Count == 0)
        {
            return NotFound();
        }

        _logger.LogInformation("Erasing timestamps for series {SeriesId} season {SeasonId} at user request", seriesId, seasonId);

        foreach (var e in episodes)
        {
            Plugin.Instance!.Intros.TryRemove(e.EpisodeId, out _);
            Plugin.Instance!.Credits.TryRemove(e.EpisodeId, out _);
            e.State.ResetStates();
            if (eraseCache)
            {
                FFmpegWrapper.DeleteEpisodeCache(e.EpisodeId);
            }
        }

        Plugin.Instance!.SaveTimestamps(AnalysisMode.Introduction | AnalysisMode.Credits);

        return NoContent();
    }

    /// <summary>
    /// Updates the ignore list for the provided season.
    /// </summary>
    /// <param name="ignoreListItem">New ignore list items.</param>
    /// <param name="save">Save the ignore list.</param>
    /// <returns>No content.</returns>
    [HttpPost("IgnoreList/UpdateSeason")]
    public ActionResult UpdateIgnoreListSeason([FromBody] IgnoreListItem ignoreListItem, bool save = true)
    {
        if (!Plugin.Instance!.QueuedMediaItems.ContainsKey(ignoreListItem.SeasonId))
        {
            return NotFound();
        }

        if (ignoreListItem.IgnoreIntro || ignoreListItem.IgnoreCredits)
        {
            Plugin.Instance!.IgnoreList.AddOrUpdate(ignoreListItem.SeasonId, ignoreListItem, (_, _) => ignoreListItem);
        }
        else
        {
            Plugin.Instance!.IgnoreList.TryRemove(ignoreListItem.SeasonId, out _);
        }

        if (save)
        {
            Plugin.Instance!.SaveIgnoreList();
        }

        return NoContent();
    }

    /// <summary>
    /// Updates the ignore list for the provided series.
    /// </summary>
    /// <param name="seriesId">Series ID.</param>
    /// <param name="ignoreListItem">New ignore list items.</param>
    /// <returns>No content.</returns>
    [HttpPost("IgnoreList/UpdateSeries/{SeriesId}")]
    public ActionResult UpdateIgnoreListSeries([FromRoute] Guid seriesId, [FromBody] IgnoreListItem ignoreListItem)
    {
        var seasonIds = Plugin.Instance!.QueuedMediaItems
            .Where(kvp => kvp.Value.Any(e => e.SeriesId == seriesId))
            .Select(kvp => kvp.Key)
            .ToList();

        if (seasonIds.Count == 0)
        {
            return NotFound();
        }

        foreach (var seasonId in seasonIds)
        {
            UpdateIgnoreListSeason(new IgnoreListItem(ignoreListItem) { SeasonId = seasonId }, false);
        }

        Plugin.Instance!.SaveIgnoreList();

        return NoContent();
    }

    /// <summary>
    /// Updates the introduction timestamps for the provided episode.
    /// </summary>
    /// <param name="id">Episode ID to update timestamps for.</param>
    /// <param name="timestamps">New introduction start and end times.</param>
    /// <response code="204">New introduction timestamps saved.</response>
    /// <returns>No content.</returns>
    [HttpPost("Episode/{Id}/UpdateIntroTimestamps")]
    [Obsolete("deprecated use Episode/{Id}/Timestamps")]
    public ActionResult UpdateIntroTimestamps([FromRoute] Guid id, [FromBody] Intro timestamps)
    {
        if (timestamps.IntroEnd > 0.0)
        {
            var tr = new TimeRange(timestamps.IntroStart, timestamps.IntroEnd);
            Plugin.Instance!.Intros[id] = new Segment(id, tr);
            Plugin.Instance.SaveTimestamps(AnalysisMode.Introduction);
        }

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
