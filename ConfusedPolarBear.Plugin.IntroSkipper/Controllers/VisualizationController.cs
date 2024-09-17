using System;
using System.Collections.Generic;
using System.Globalization;
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
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Intros")]
public class VisualizationController : ControllerBase
{
    private readonly ILogger<VisualizationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VisualizationController"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public VisualizationController(ILogger<VisualizationController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns all show names and seasons.
    /// </summary>
    /// <returns>Dictionary of show names to a list of season names.</returns>
    [HttpGet("Shows")]
    public ActionResult<Dictionary<string, HashSet<string>>> GetShowSeasons()
    {
        _logger.LogDebug("Returning season names by series");

        var showSeasons = new Dictionary<string, HashSet<string>>();

        // Loop through all seasons in the analysis queue
        foreach (var kvp in Plugin.Instance!.QueuedMediaItems)
        {
            // Check that this season contains at least one episode.
            var episodes = kvp.Value;
            if (episodes is null || episodes.Count == 0)
            {
                _logger.LogDebug("Skipping season {Id} (null or empty)", kvp.Key);
                continue;
            }

            // Peek at the top episode from this season and store the series name and season number.
            var first = episodes[0];
            var series = first.SeriesName;
            var season = GetSeasonName(first);

            // Validate the series and season before attempting to store it.
            if (string.IsNullOrWhiteSpace(series) || string.IsNullOrWhiteSpace(season))
            {
                _logger.LogDebug("Skipping season {Id} (no name or number)", kvp.Key);
                continue;
            }

            // TryAdd is used when adding the HashSet since it is a no-op if one was already created for this series.
            showSeasons.TryAdd(series, new HashSet<string>());
            showSeasons[series].Add(season);
        }

        return showSeasons;
    }

    /// <summary>
    /// Returns the ignore list for the provided season.
    /// </summary>
    /// <param name="series">Show name.</param>
    /// <param name="season">Season name.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("IgnoreList/{Series}/{Season}")]
    public ActionResult<IgnoreListItem> GetIgnoreListSeason([FromRoute] string series, [FromRoute] string season)
    {
        if (!LookupSeasonIdByName(series, season, out var seasonId))
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
    /// <param name="series">Show name.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("IgnoreList/{Series}")]
    public ActionResult<IgnoreListItem> GetIgnoreListSeries([FromRoute] string series)
    {
        if (!LookupSeasonIdsByName(series, out var seasonIds))
        {
            return NotFound();
        }

        IgnoreListItem ignoreListItem = new IgnoreListItem(Guid.Empty);
        foreach (var seasonId in seasonIds)
        {
            ignoreListItem.IgnoreIntro |= Plugin.Instance!.IsIgnored(seasonId, AnalysisMode.Introduction);
            ignoreListItem.IgnoreCredits |= Plugin.Instance!.IsIgnored(seasonId, AnalysisMode.Credits);

            if (ignoreListItem.IgnoreIntro && ignoreListItem.IgnoreCredits)
            {
                break;
            }
        }

        return ignoreListItem;
    }

    /// <summary>
    /// Returns the names and unique identifiers of all episodes in the provided season.
    /// </summary>
    /// <param name="series">Show name.</param>
    /// <param name="season">Season name.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("Show/{Series}/{Season}")]
    public ActionResult<List<EpisodeVisualization>> GetSeasonEpisodes([FromRoute] string series, [FromRoute] string season)
    {
        var visualEpisodes = new List<EpisodeVisualization>();

        if (!LookupSeasonByName(series, season, out var episodes))
        {
            return NotFound();
        }

        foreach (var e in episodes)
        {
            visualEpisodes.Add(new EpisodeVisualization(e.EpisodeId, e.Name));
        }

        return visualEpisodes;
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
    /// <param name="series">Show name.</param>
    /// <param name="season">Season name.</param>
    /// <param name="eraseCache">Erase cache.</param>
    /// <response code="204">Season timestamps erased.</response>
    /// <response code="404">Unable to find season in provided series.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Show/{Series}/{Season}")]
    public ActionResult EraseSeason([FromRoute] string series, [FromRoute] string season, [FromQuery] bool eraseCache = false)
    {
        if (!LookupSeasonByName(series, season, out var episodes))
        {
            return NotFound();
        }

        _logger.LogInformation("Erasing timestamps for {Series} {Season} at user request", series, season);

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

        Plugin.Instance!.SaveTimestamps(AnalysisMode.Introduction);
        Plugin.Instance!.SaveTimestamps(AnalysisMode.Credits);

        return NoContent();
    }

    /// <summary>
    /// Updates the ignore list for the provided season.
    /// </summary>
    /// <param name="ignoreListItem">New ignore list items.</param>
    /// <param name="save">Save the ignore list.</param>
    /// <returns>No content.</returns>
    [HttpPost("IgnoreList/UpdateIgnoreListSeason")]
    public ActionResult UpdateIgnoreListSeason([FromBody] IgnoreListItem ignoreListItem, bool save = true)
    {
        if (!Plugin.Instance!.QueuedMediaItems.ContainsKey(ignoreListItem.Id))
        {
            return NotFound();
        }

        Plugin.Instance!.IgnoreList[ignoreListItem.Id] = ignoreListItem;

        if (save)
        {
            Plugin.Instance!.SaveIgnoreList();
        }

        return NoContent();
    }

    /// <summary>
    /// Updates the ignore list for the provided series.
    /// </summary>
    /// <param name="series">Series name.</param>
    /// <param name="ignoreListItem">New ignore list items.</param>
    /// <returns>No content.</returns>
    [HttpPost("IgnoreList/UpdateIgnoreListSeries/{Series}")]
    public ActionResult UpdateIgnoreListSeries([FromRoute] string series, [FromBody] IgnoreListItem ignoreListItem)
    {
        if (!LookupSeasonIdsByName(series, out var seasonIds))
        {
            return NotFound();
        }

        foreach (var seasonId in seasonIds)
        {
            UpdateIgnoreListSeason(new IgnoreListItem(ignoreListItem) { Id = seasonId }, false);
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

    private static string GetSeasonName(QueuedEpisode episode)
    {
        return "Season " + episode.SeasonNumber.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Lookup a named season of a series and return all queued episodes.
    /// </summary>
    /// <param name="series">Series name.</param>
    /// <param name="season">Season name.</param>
    /// <param name="episodes">Episodes.</param>
    /// <returns>Boolean indicating if the requested season was found.</returns>
    private static bool LookupSeasonByName(string series, string season, out List<QueuedEpisode> episodes)
    {
        foreach (var queuedEpisodes in Plugin.Instance!.QueuedMediaItems)
        {
            var first = queuedEpisodes.Value[0];
            var firstSeasonName = GetSeasonName(first);

            // Assert that the queued episode series and season are equal to what was requested
            if (
                !string.Equals(first.SeriesName, series, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(firstSeasonName, season, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            episodes = queuedEpisodes.Value;
            return true;
        }

        episodes = [];
        return false;
    }

    /// <summary>
    /// Lookup a named season of a series and return its season id.
    /// </summary>
    /// <param name="series">Series name.</param>
    /// <param name="season">Season name.</param>
    /// <param name="seasonId">Season id.</param>
    /// <returns>Boolean indicating if the requested season was found.</returns>
    private bool LookupSeasonIdByName(string series, string season, out Guid seasonId)
    {
        foreach (var queuedEpisodes in Plugin.Instance!.QueuedMediaItems)
        {
            var first = queuedEpisodes.Value[0];
            var firstSeasonName = GetSeasonName(first);

            // Assert that the queued episode series and season are equal to what was requested
            if (
                !string.Equals(first.SeriesName, series, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(firstSeasonName, season, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            seasonId = queuedEpisodes.Key;
            return true;
        }

        seasonId = Guid.Empty;
        return false;
    }

    /// <summary>
    /// Lookup a named series and return all the season ids.
    /// </summary>
    /// <param name="series">Series name.</param>
    /// <param name="seasons">Seasons.</param>
    /// <returns>Boolean indicating if the requested series was found.</returns>
    private bool LookupSeasonIdsByName(string series, out List<Guid> seasons)
    {
        seasons = new List<Guid>();

        foreach (var queuedEpisodes in Plugin.Instance!.QueuedMediaItems)
        {
            var first = queuedEpisodes.Value[0];

            // Assert that the queued episode series is equal to what was requested
            if (!string.Equals(first.SeriesName, series, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            seasons.Add(queuedEpisodes.Key);
        }

        return seasons.Count > 0;
    }
}
