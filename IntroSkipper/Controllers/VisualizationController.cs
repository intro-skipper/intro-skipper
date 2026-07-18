// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Audio fingerprint visualization controller. Allows browsing fingerprints on a per episode basis.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="VisualizationController"/> class.
/// </remarks>
/// <param name="logger">Logger.</param>
/// <param name="mediaSegmentRefresher">Media segment refresher.</param>
/// <param name="libraryManager">libraryManager.</param>
/// <param name="providerManager">providerManager.</param>
/// <param name="fileSystem">fileSystem.</param>
/// <param name="loggerFactory">loggerFactory.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="cacheDatabase">Detection cache database facade.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Intros")]
public partial class VisualizationController(ILogger<VisualizationController> logger, IMediaSegmentRefresher mediaSegmentRefresher, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILoggerFactory loggerFactory, IFFmpegService ffmpegService, IDetectionCacheService cacheService, IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase) : ControllerBase
{
    private readonly ILogger<VisualizationController> _logger = logger;
    private readonly IMediaSegmentRefresher _mediaSegmentRefresher = mediaSegmentRefresher;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IProviderManager _providerManager = providerManager;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IFFmpegService _ffmpegService = ffmpegService;
    private readonly IDetectionCacheService _cacheService = cacheService;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;

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

        var analyzerActions = await _database.GetAllAnalyzerActionsAsync(seasonId, cancellationToken).ConfigureAwait(false);

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
        if (!Plugin.Instance!.QueuedMediaItems.TryGetValue(seasonId, out var episodes))
        {
            return NotFound();
        }

        if (episodes.Count == 0)
        {
            return NotFound();
        }

        LogErasingTimestamps(_logger, seriesId, seasonId);

        try
        {
            var episodeIds = episodes.Select(e => e.EpisodeId).ToHashSet();
            await _database.ClearSeasonAnalysisAsync(seasonId, episodeIds, cancellationToken).ConfigureAwait(false);

            if (eraseCache)
            {
                // Best-effort cache cleanup (the facade logs and swallows database errors),
                // not bound to request cancellation: the main database is already consistent.
                await _cacheDatabase.DeleteForItemsAsync(episodeIds, CancellationToken.None).ConfigureAwait(false);
            }

            if (Plugin.Instance.Configuration.UpdateMediaSegments)
            {
                await _mediaSegmentRefresher.RefreshAsync(episodeIds, cancellationToken).ConfigureAwait(false);
            }

            return NoContent();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailedToEraseTimestamps(_logger, ex, seriesId, seasonId);
            return Problem("An unexpected error occurred while erasing season data.", statusCode: StatusCodes.Status500InternalServerError);
        }
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
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is null");

        try
        {
            var excludedItems = await GetExcludedInventoryAsync(plugin, cancellationToken).ConfigureAwait(false);
            var excludedIds = excludedItems.Select(e => e.EpisodeId).ToHashSet();
            if (excludedIds.Count == 0)
            {
                return Ok(new ClearExcludedTimestampsResponse(0, 0, 0));
            }

            var excludedIdsBySeason = excludedItems
                .GroupBy(e => e.SeasonId)
                .ToDictionary(g => g.Key, g => (IReadOnlySet<Guid>)g.Select(e => e.EpisodeId).ToHashSet());

            var removedSegments = await _database
                .RemoveItemsFromAnalysisAsync(excludedIdsBySeason, cancellationToken)
                .ConfigureAwait(false);

            // Best-effort cache cleanup (the facade logs and swallows database errors),
            // not bound to request cancellation: the main database transaction has
            // committed, so make one complete attempt.
            var removedCacheEntries = await _cacheDatabase
                .DeleteForItemsAsync(excludedIds, CancellationToken.None)
                .ConfigureAwait(false);

            if (plugin.Configuration.UpdateMediaSegments)
            {
                await _mediaSegmentRefresher.RefreshAsync(excludedIds, cancellationToken).ConfigureAwait(false);
            }

            return Ok(new ClearExcludedTimestampsResponse(excludedIds.Count, removedSegments, removedCacheEntries));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailedToClearExcludedTimestamps(_logger, ex);
            return Problem("An unexpected error occurred while clearing excluded timestamp data.", statusCode: StatusCodes.Status500InternalServerError);
        }
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

    private async Task<IReadOnlyList<QueuedEpisode>> GetExcludedInventoryAsync(Plugin plugin, CancellationToken cancellationToken)
    {
        if (_libraryManager is not null)
        {
            var queueManager = new QueueManager(
                _loggerFactory.CreateLogger<QueueManager>(),
                _libraryManager,
                _providerManager,
                _fileSystem,
                _ffmpegService,
                _database);
            var queue = await queueManager.GetMediaItems(includeExcluded: true, cancellationToken).ConfigureAwait(false);
            return [.. queue.Values.SelectMany(static episodes => episodes).Where(static episode => episode.IsExcluded)];
        }

        var policy = ExclusionPolicy.FromConfiguration(plugin.Configuration);
        return [.. plugin.QueuedMediaItems.Values
            .SelectMany(static episodes => episodes)
            .Where(episode => IsExcludedByPolicy(policy, episode))];
    }

    private static bool IsExcludedByPolicy(ExclusionPolicy policy, QueuedEpisode episode)
    {
        var decision = episode.Category == QueuedMediaCategory.Movie
            ? policy.EvaluateMovie(episode.Name, episode.EpisodeId, episode.Path)
            : policy.EvaluateSeries(episode.SeriesName, episode.SeriesId, episode.Path);
        return decision.IsExcluded;
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
    /// <param name="cancellationToken">cancellationToken.</param>
    /// <returns>Accepted if the scan was started; Conflict if a scan is already running.</returns>
    [HttpPost("ScanSeason/{SeriesId}/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> ScanSeason([FromRoute] Guid seriesId, [FromRoute] Guid seasonId, CancellationToken cancellationToken = default)
    {
        if (_libraryManager is null)
        {
            throw new InvalidOperationException("Library manager was null");
        }

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
                        await EraseSeasonAsync(seriesId, seasonId, true, CancellationToken.None).ConfigureAwait(false);

                        var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                            _loggerFactory.CreateLogger<DetectSegmentsTask>(),
                            _loggerFactory,
                            _libraryManager,
                            _providerManager,
                            _fileSystem,
                            _mediaSegmentRefresher,
                            _ffmpegService,
                            _cacheService,
                            _database);

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to clear excluded timestamp data")]
    private static partial void LogFailedToClearExcludedTimestamps(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Start (Re-) scan of season/movie {SeasonId}")]
    private static partial void LogStartRescan(ILogger logger, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Manual season rescan for {SeasonId} was canceled.")]
    private static partial void LogRescanCanceled(ILogger logger, Guid seasonId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during manual season rescan for {SeasonId}")]
    private static partial void LogRescanError(ILogger logger, Exception ex, Guid seasonId);
}
