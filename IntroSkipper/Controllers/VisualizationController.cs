// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.SegmentChanges;
using IntroSkipper.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
/// <param name="segmentChange">Durable segment-change coordinator; owns the visibility mutation and converges journaled projections.</param>
/// <param name="queue">Analysis queue a manual scan is handed to, and whose status the scan status endpoint reports.</param>
/// <param name="seasonResolver">Resolver of season keys into the episodes the dashboard shows, so every endpoint answers from the live library.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="eraser">Erases seasons' segments, analysis state and cache rows, and converges their mirrors.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Intros")]
public partial class VisualizationController(ILogger<VisualizationController> logger, SegmentChange segmentChange, AnalysisScheduler queue, SeasonResolver seasonResolver, IIntroSkipperDatabase database, ISegmentEraser eraser) : ControllerBase
{
    private readonly ILogger<VisualizationController> _logger = logger;
    private readonly SegmentChange _segmentChange = segmentChange;
    private readonly AnalysisScheduler _queue = queue;
    private readonly SeasonResolver _seasonResolver = seasonResolver;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly ISegmentEraser _eraser = eraser;

    /// <summary>
    /// Returns the analyzer actions for the provided season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analyzer actions for the season.</returns>
    [HttpGet("AnalyzerActions/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>>> GetAnalyzerAction([FromRoute] Guid seasonId, CancellationToken cancellationToken = default)
    {
        if (!_seasonResolver.IsKnownKey(seasonId))
        {
            return NotFound();
        }

        var analyzerActions = await _database.GetAllAnalyzerActionsAsync(seasonId, cancellationToken).ConfigureAwait(false);

        return Ok(analyzerActions);
    }

    /// <summary>
    /// Returns the names and unique identifiers of the episodes Jellyfin shows under the provided season.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("Show/{SeriesId}/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<List<EpisodeVisualization>> GetSeasonEpisodes([FromRoute] Guid seriesId, [FromRoute] Guid seasonId)
    {
        if (_seasonResolver.ResolveDisplayed(seasonId) is not { } season || season.SeriesId != seriesId)
        {
            return NotFound();
        }

        return season.Episodes.Select(e => new EpisodeVisualization(e.EpisodeId, e.Name)).ToList();
    }

    /// <summary>
    /// Erases all timestamps for the provided season.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="eraseCache">Erase cache.</param>
    /// <param name="cancellationToken">Cancellation Token.</param>
    /// <response code="204">Season timestamps erased, or the season has nothing to erase.</response>
    /// <response code="404">The season id is not a season or movie of the series the server knows.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Show/{SeriesId}/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> EraseSeasonAsync([FromRoute] Guid seriesId, [FromRoute] Guid seasonId, [FromQuery] bool eraseCache = false, CancellationToken cancellationToken = default)
    {
        if (_seasonResolver.ResolveDisplayed(seasonId) is not { } season || season.SeriesId != seriesId)
        {
            return NotFound();
        }

        // A known season with nothing to erase is a no-op.
        if (season.Episodes.Count > 0)
        {
            LogErasingTimestamps(_logger, seriesId, seasonId);
            await _eraser.EraseItemsAsync(season.Episodes.Select(e => e.EpisodeId).ToHashSet(), eraseCache, cancellationToken).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Clears timestamp, cache, and season-state data for media matched by the current exclusion policy.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counts describing the cleared excluded timestamp state.</returns>
    [HttpPost("ExcludedTimestamps/Clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ClearExcludedTimestampsResponse>> ClearExcludedTimestampsAsync(CancellationToken cancellationToken = default)
    {
        var excludedIds = _seasonResolver.ResolveLibrary(includeExcluded: true, cancellationToken).Seasons
            .SelectMany(static season => season.Episodes)
            .Where(static episode => episode.IsExcluded)
            .Select(static episode => episode.EpisodeId)
            .ToHashSet();
        if (excludedIds.Count == 0)
        {
            return Ok(new ClearExcludedTimestampsResponse(0, 0, 0));
        }

        var (removedSegments, removedCacheEntries) = await _eraser.EraseItemsAsync(excludedIds, eraseCache: true, cancellationToken).ConfigureAwait(false);
        return Ok(new ClearExcludedTimestampsResponse(excludedIds.Count, removedSegments, removedCacheEntries));
    }

    /// <summary>
    /// Updates the analyzer actions for the provided season.
    /// </summary>
    /// <param name="request">Update analyzer actions request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("AnalyzerActions/UpdateSeason")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> UpdateAnalyzerActions([FromBody] UpdateAnalyzerActionsRequest request, CancellationToken cancellationToken = default)
    {
        await _database.SetAnalyzerActionAsync(request.Id, request.AnalyzerActions, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Returns the optional analysis-window overrides for the provided season.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The optional overrides for the season.</returns>
    [HttpGet("AnalysisOverrides/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalysisOverrides>> GetAnalysisOverrides([FromRoute] Guid seasonId, CancellationToken cancellationToken = default)
    {
        if (!_seasonResolver.IsKnownKey(seasonId))
        {
            return NotFound();
        }

        return Ok(await _database.GetAnalysisOverridesAsync(seasonId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Updates the optional analysis-window overrides for the provided season.
    /// </summary>
    /// <param name="request">Analysis override update request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content when the overrides are saved.</returns>
    [HttpPost("AnalysisOverrides/UpdateSeason")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateAnalysisOverrides([FromBody] UpdateAnalysisOverridesRequest request, CancellationToken cancellationToken = default)
    {
        if (!_seasonResolver.IsKnownKey(request.Id))
        {
            return NotFound();
        }

        if (request.AnalysisPercent is < PluginConfiguration.MinimumAnalysisPercent or > PluginConfiguration.MaximumAnalysisPercent
            || request.AnalysisLengthLimit is < 1)
        {
            return BadRequest("Analysis overrides are outside the supported range.");
        }

        await _database.SetAnalysisOverridesAsync(request.Id, request.AnalysisPercent, request.AnalysisLengthLimit, request.PreviewFromCreditsEnd, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Returns the IDs of the items Jellyfin shows under the season whose automatic
    /// segments are withheld from Jellyfin. Unknown or empty seasons yield an empty set
    /// rather than an error.
    /// </summary>
    /// <param name="seasonId">Season ID (a movie's own ID for movies).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The disabled item IDs.</returns>
    [HttpGet("DisabledItems/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlySet<Guid>>> GetDisabledItems([FromRoute] Guid seasonId, CancellationToken cancellationToken = default)
    {
        var itemIds = _seasonResolver.ResolveDisplayed(seasonId)?.ItemIds ?? [];
        return Ok(await _database.GetDisabledItemIdsAsync(itemIds, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Withholds the item's automatic segments from Jellyfin. Analysis and stored
    /// segments are unaffected; user-provided segments keep syncing.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPut("DisabledItems/{ItemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public Task<ActionResult> DisableItem([FromRoute] Guid itemId, CancellationToken cancellationToken = default)
    {
        return SetItemDisabledAsync(itemId, disabled: true, cancellationToken);
    }

    /// <summary>
    /// Restores the item's automatic segments to Jellyfin without re-analysis.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("DisabledItems/{ItemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public Task<ActionResult> EnableItem([FromRoute] Guid itemId, CancellationToken cancellationToken = default)
    {
        return SetItemDisabledAsync(itemId, disabled: false, cancellationToken);
    }

    private async Task<ActionResult> SetItemDisabledAsync(Guid itemId, bool disabled, CancellationToken cancellationToken)
    {
        if (MediaItemHelper.FindSupported(itemId) is null)
        {
            return NotFound();
        }

        // The coordinator commits the flag durably with its projection work in one
        // transaction; a failed or skipped Jellyfin resync never rolls the flag back,
        // the journaled work converges the mirror instead. Only a failure to commit
        // throws, and nothing was changed then.
        var outcome = await _segmentChange
            .ApplyAsync(new SegmentVisibilityChangeIntent(itemId, Visible: !disabled), cancellationToken)
            .ConfigureAwait(false);
        // An idempotent toggle succeeds too (its journaled re-projection still
        // heals a diverged mirror).
        return SegmentChangeHttp.Map(outcome, onApplied: _ => NoContent());
    }

    /// <summary>
    /// Returns whether a scan is running (a pass in flight, or a manual scan or library
    /// pass waiting for the worker; library changes waiting out their quiet period do not
    /// count), whether the season's own scan is pending or running, which the dashboard
    /// polls until its scan has run, and whether its most recent scan failed.
    /// </summary>
    /// <param name="seasonId">Season ID (a movie's own ID for movies).</param>
    /// <returns>The scan status.</returns>
    [HttpGet("ScanStatus/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ScanStatusResponse> GetScanStatus([FromRoute] Guid seasonId)
    {
        var scan = _queue.ScanStatus(seasonId);
        return new ScanStatusResponse(_queue.Status.IsRunning, scan.Queued, scan.Failed);
    }

    /// <summary>
    /// Queues a manual scan of the episodes Jellyfin shows under the provided season: as
    /// its own pass once any pass in flight has finished, the queue resolves the season
    /// again, erases its timestamps and cache, then analyzes each episode in the season it
    /// is analyzed in, which for an in-season special is its host season. A repeat
    /// request joins the season's pending scan; one made while its scan runs queues a
    /// follow-up.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <returns>Accepted once the scan is queued; Not Found if the id is not a season or movie of the series the server knows.</returns>
    [HttpPost("ScanSeason/{SeriesId}/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ScanSeason([FromRoute] Guid seriesId, [FromRoute] Guid seasonId)
    {
        if (_seasonResolver.ResolveDisplayed(seasonId) is not { } season || season.SeriesId != seriesId)
        {
            return NotFound();
        }

        LogStartRescan(_logger, seasonId);

        // The handle is dropped: the request has already returned, and the queue logs a
        // failed pass itself.
        _ = _queue.ScanAsync(seasonId);

        return Accepted();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Erasing timestamps for series {SeriesId} season {SeasonId} at user request")]
    private static partial void LogErasingTimestamps(ILogger logger, Guid seriesId, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued a manual scan of season/movie {SeasonId}")]
    private static partial void LogStartRescan(ILogger logger, Guid seasonId);
}
