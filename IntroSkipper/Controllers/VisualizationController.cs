// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Repositories;
using IntroSkipper.ScheduledTasks;
using IntroSkipper.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
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
/// <param name="libraryManager">libraryManager.</param>
/// <param name="loggerFactory">loggerFactory.</param>
/// <param name="serviceProvider">Service provider.</param>
/// <param name="segmentService">Segment service.</param>
/// <param name="seasonRepository">Season repository.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Intros")]
public class VisualizationController(
    ILogger<VisualizationController> logger,
    ILibraryManager libraryManager,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider,
    ISegmentService segmentService,
    ISeasonRepository seasonRepository) : ControllerBase
{
    private readonly ILogger<VisualizationController> _logger = logger;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ISegmentService _segmentService = segmentService;
    private readonly ISeasonRepository _seasonRepository = seasonRepository;

    /// <summary>
    /// Returns all show names and seasons.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary of show names to a list of season names.</returns>
    [HttpGet("Shows")]
    public async Task<ActionResult<Dictionary<Guid, ShowInfos>>> GetShowSeasons(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Returning season IDs by series name");

        // Ensure the queue is up to date
        await new QueueManager(_loggerFactory.CreateLogger<QueueManager>(), _libraryManager, _serviceProvider).GetMediaItems(cancellationToken).ConfigureAwait(false);

        var showSeasons = new Dictionary<Guid, ShowInfos>();

        foreach (var kvp in Plugin.Instance!.QueuedMediaItems)
        {
            if (kvp.Value.FirstOrDefault() is not QueuedEpisode first)
            {
                continue;
            }

            var seriesId = first.SeriesId;
            var seasonId = kvp.Key;

            var seasonNumber = first.SeasonNumber;
            if (!showSeasons.TryGetValue(seriesId, out var showInfo))
            {
                showInfo = new ShowInfos
                {
                    SeriesName = first.SeriesName,
                    ProductionYear = GetProductionYear(seriesId),
                    LibraryName = GetLibraryName(seriesId),
                    IsMovie = IsMovie(first),
                    Seasons = []
                };
                showSeasons[seriesId] = showInfo;
            }

            showInfo.Seasons[seasonId] = seasonNumber;
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of episode titles.</returns>
    [HttpGet("AnalyzerActions/{SeasonId}")]
    public async Task<ActionResult<IReadOnlyDictionary<AnalysisMode, AnalyzerAction>>> GetAnalyzerActionAsync([FromRoute] Guid seasonId, CancellationToken cancellationToken = default)
    {
        if (!Plugin.Instance!.QueuedMediaItems.ContainsKey(seasonId))
        {
            return NotFound();
        }

        var analyzerActions = new Dictionary<AnalysisMode, AnalyzerAction>();
        foreach (var mode in Enum.GetValues<AnalysisMode>())
        {
            analyzerActions[mode] = await _seasonRepository.GetAnalyzerActionAsync(seasonId, mode, cancellationToken).ConfigureAwait(false);
        }

        return Ok(analyzerActions);
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
        if (!Plugin.Instance!.QueuedMediaItems.TryGetValue(seasonId, out var episodes))
        {
            return NotFound();
        }

        if (episodes.Count == 0)
        {
            return NotFound();
        }

        _logger.LogInformation("Erasing timestamps for series {SeriesId} season {SeasonId} at user request", seriesId, seasonId);

        try
        {
            // Delete all segments for the season in a single batch operation
            await _segmentService.DeleteSeasonSegmentsAsync(seasonId, cancellationToken).ConfigureAwait(false);

            // Erase fingerprint cache if requested
            if (eraseCache)
            {
                foreach (var episode in episodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Run(() => FFmpegWrapper.DeleteFingerprintCache(episode.EpisodeId), cancellationToken).ConfigureAwait(false);
                }
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to erase timestamps for series {SeriesId} season {SeasonId}", seriesId, seasonId);
            return Problem("An unexpected error occurred while erasing season data.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Updates the analyzer actions for the provided season.
    /// </summary>
    /// <param name="request">Update analyzer actions request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("AnalyzerActions/UpdateSeason")]
    public async Task<ActionResult> UpdateAnalyzerActionsAsync([FromBody] UpdateAnalyzerActionsRequest request, CancellationToken cancellationToken = default)
    {
        await _seasonRepository.SetAnalyzerActionsAsync(request.Id, request.AnalyzerActions, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Scans the provided season for intros.
    /// </summary>
    /// <param name="seriesId">Show ID.</param>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="cancellationToken">cancellationToken.</param>
    /// <returns>No content.</returns>
    [HttpPost("ScanSeason/{SeriesId}/{SeasonId}")]
    public ActionResult ScanSeason([FromRoute] Guid seriesId, [FromRoute] Guid seasonId, CancellationToken cancellationToken = default)
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

        // Run erase + analyze in background so it doesn't get canceled when the HTTP request ends/timeouts
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // Do not bind to the HTTP request cancellation; long-running job should complete even if client disconnects
                    using (await ScheduledTaskSemaphore.AcquireAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        _logger.LogInformation("Start (Re-) scan of season/movie {Season}", seasonId);

                        // Erase season timestamps and cache first
                        await EraseSeasonAsync(seriesId, seasonId, true, CancellationToken.None).ConfigureAwait(false);

                        var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                            _loggerFactory.CreateLogger<DetectSegmentsTask>(),
                            _loggerFactory,
                            _libraryManager,
                            _serviceProvider);

                        await baseIntroAnalyzer.AnalyzeItemsAsync(new Progress<double>(), CancellationToken.None, [seasonId]).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Manual season rescan for {SeasonId} was canceled.", seasonId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during manual season rescan for {SeasonId}", seasonId);
                }
            },
            CancellationToken.None);

        // Immediately return to the client; background task continues
        return Accepted();
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

    private static bool IsMovie(QueuedEpisode episode) => episode.Category == QueuedMediaCategory.Movie;
}
