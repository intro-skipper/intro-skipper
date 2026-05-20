// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Audio fingerprint visualization controller. Allows browsing fingerprints on a per episode basis.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="VisualizationController"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="mediaSegmentUpdateManager">Media segment update manager.</param>
/// <param name="serviceScopeFactory">Service scope factory for background analyzer tasks.</param>
/// <param name="cacheService">Detection cache service.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Intros")]
public partial class VisualizationController(
    ILogger<VisualizationController> logger,
    MediaSegmentUpdateManager mediaSegmentUpdateManager,
    IServiceScopeFactory serviceScopeFactory,
    IDetectionCacheService cacheService) : ControllerBase
{
    private readonly ILogger<VisualizationController> _logger = logger;
    private readonly MediaSegmentUpdateManager _mediaSegmentUpdateManager = mediaSegmentUpdateManager;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly IDetectionCacheService _cacheService = cacheService;

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
        if (!Plugin.Instance!.QueuedMediaItems.ContainsKey(seasonId))
        {
            return NotFound();
        }

        var analyzerActions = await Plugin.Instance!.GetAllAnalyzerActionsAsync(seasonId, cancellationToken).ConfigureAwait(false);

        return Ok(analyzerActions);
    }

    /// <summary>
    /// Returns the names and unique identifiers of all episodes in the provided season.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("Show/{SeriesId}/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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

        return episodes.Select(e => new EpisodeVisualization(e.EpisodeId, e.Name)).ToList();
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
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> EraseSeasonAsync([FromRoute] Guid seriesId, [FromRoute] Guid seasonId, [FromQuery] bool eraseCache = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await EraseSeasonDataAsync(seriesId, seasonId, eraseCache, cancellationToken).ConfigureAwait(false))
            {
                return NotFound();
            }

            return NoContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailedToEraseTimestamps(_logger, ex, seriesId, seasonId);
            return Problem("An unexpected error occurred while erasing season data.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Core erase logic shared by the HTTP action and background rescan.
    /// Deletes all segments for the season, optionally erases the fingerprint cache,
    /// clears analyzed-state lists, and updates media segments if configured.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="eraseCache">Whether to erase fingerprint cache files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if the season was found and erased; <see langword="false"/> if not found.</returns>
    private async Task<bool> EraseSeasonDataAsync(Guid seriesId, Guid seasonId, bool eraseCache, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance!.QueuedMediaItems.TryGetValue(seasonId, out var episodes) || episodes.Count == 0)
        {
            return false;
        }

        LogErasingTimestamps(_logger, seriesId, seasonId);

        using var db = Plugin.CreateDbContext();

        // ExecuteDeleteAsync runs a single server-side DELETE and bypasses the change tracker.
        // This is safe here because the tracked operations below target DbSeasonInfo, not DbSegment.
        var episodeIds = episodes.Select(e => e.EpisodeId).ToHashSet();
        await db.DbSegment
            .Where(s => episodeIds.Contains(s.ItemId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (eraseCache)
        {
            // Cache deletion must run to completion, the DB rows are already gone,
            // so aborting here would leave orphaned files with no way to clean them up.
            foreach (var episode in episodes)
            {
                await _cacheService.DeleteFingerprintCacheAsync(episode.EpisodeId, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Batch-load season info and clear episode IDs
        var seasonInfos = await db.DbSeasonInfo
            .Where(s => s.SeasonId == seasonId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var info in seasonInfos)
        {
            db.Entry(info).Property(s => s.EpisodeIds).CurrentValue = [];
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (Plugin.Instance.Configuration.UpdateMediaSegments)
        {
            await _mediaSegmentUpdateManager.UpdateMediaSegmentsAsync(episodes, cancellationToken).ConfigureAwait(false);
        }

        return true;
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
        await Plugin.Instance!.SetAnalyzerActionAsync(request.Id, request.AnalyzerActions, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Returns whether a scan is currently running.
    /// </summary>
    /// <returns>A JSON object indicating whether a scan is currently in progress.</returns>
    [HttpGet("ScanStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ScanStatusResponse> GetScanStatus()
    {
        return new ScanStatusResponse(ScheduledTaskSemaphore.IsBusy);
    }

    /// <summary>
    /// Scans the provided season for intros.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <returns>Accepted if the scan was started; Conflict if a scan is already running.</returns>
    [HttpPost("ScanSeason/{SeriesId}/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> ScanSeason([FromRoute] Guid seriesId, [FromRoute] Guid seasonId)
    {
        var scanLease = await ScheduledTaskSemaphore.TryAcquireAsync().ConfigureAwait(false);
        if (scanLease is null)
        {
            return Conflict(new { message = "A scan is already in progress." });
        }

        // Run erase + analyze in background so it doesn't get canceled when the HTTP request ends/timeouts
        _ = Task.Run(
            async () =>
            {
                using (scanLease)
                {
                    try
                    {
                        // Do not bind to the HTTP request cancellation; long-running job should complete even if client disconnects
                        LogStartRescan(_logger, seasonId);

                        // Erase season timestamps and cache first
                        await EraseSeasonDataAsync(seriesId, seasonId, eraseCache: true, CancellationToken.None).ConfigureAwait(false);

                        using var scope = _serviceScopeFactory.CreateScope();
                        var baseIntroAnalyzer = ActivatorUtilities.CreateInstance<BaseItemAnalyzerTask>(scope.ServiceProvider);

                        await baseIntroAnalyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None, [seasonId]).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        LogRescanCanceled(_logger, seasonId);
                    }
                    catch (Exception ex)
                    {
                        LogRescanError(_logger, ex, seasonId);
                    }
                }
            },
            CancellationToken.None);

        // Immediately return to the client; background task continues
        return Accepted();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Erasing timestamps for series {SeriesId} season {SeasonId} at user request")]
    private static partial void LogErasingTimestamps(ILogger logger, Guid seriesId, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to erase timestamps for series {SeriesId} season {SeasonId}")]
    private static partial void LogFailedToEraseTimestamps(ILogger logger, Exception ex, Guid seriesId, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Start (Re-) scan of season/movie {SeasonId}")]
    private static partial void LogStartRescan(ILogger logger, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Manual season rescan for {SeasonId} was canceled.")]
    private static partial void LogRescanCanceled(ILogger logger, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during manual season rescan for {SeasonId}")]
    private static partial void LogRescanError(ILogger logger, Exception ex, Guid seasonId);
}
