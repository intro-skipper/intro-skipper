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
using IntroSkipper.SegmentChanges;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
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
/// <param name="segmentChange">Durable segment-change coordinator; owns the visibility mutation and converges journaled projections.</param>
/// <param name="libraryManager">libraryManager.</param>
/// <param name="analyzerFactory">Factory for per-run queue managers and analyzer tasks.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="cacheDatabase">Detection cache database facade.</param>
/// <param name="taskManager">Scheduled task manager, used to report the detection task's state.</param>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("Intros")]
public partial class VisualizationController(ILogger<VisualizationController> logger, ISegmentChange segmentChange, ILibraryManager libraryManager, AnalyzerTaskFactory analyzerFactory, IIntroSkipperDatabase database, IDetectionCacheDatabase cacheDatabase, ITaskManager taskManager) : ControllerBase
{
    private readonly ILogger<VisualizationController> _logger = logger;
    private readonly ISegmentChange _segmentChange = segmentChange;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly AnalyzerTaskFactory _analyzerFactory = analyzerFactory;
    private readonly IIntroSkipperDatabase _database = database;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly ITaskManager _taskManager = taskManager;

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
            await _database.EraseItemsAsync(episodeIds, cancellationToken).ConfigureAwait(false);

            if (eraseCache)
            {
                // Best-effort cache cleanup (the facade logs and swallows database errors),
                // not bound to request cancellation: the main database is already consistent.
                await _cacheDatabase.DeleteForItemsAsync(episodeIds, CancellationToken.None).ConfigureAwait(false);
            }

            // The erase journaled every affected item's projection; converge exactly
            // those items now — unrelated pending work keeps its backoff. Anything
            // this pass cannot finish stays journaled and the worker completes it.
            foreach (var episodeId in episodeIds)
            {
                await _segmentChange.RetryProjectionAsync(ProjectionScope.ForItem(episodeId), cancellationToken).ConfigureAwait(false);
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

            var removedSegments = await _database
                .EraseItemsAsync(excludedIds, cancellationToken)
                .ConfigureAwait(false);

            // Best-effort cache cleanup (the facade logs and swallows database errors),
            // not bound to request cancellation: the main database transaction has
            // committed, so make one complete attempt.
            var removedCacheEntries = await _cacheDatabase
                .DeleteForItemsAsync(excludedIds, CancellationToken.None)
                .ConfigureAwait(false);

            // As in EraseSeasonAsync: the erase journaled the affected items'
            // projections; converge exactly those items now, the journal owns the rest.
            foreach (var excludedId in excludedIds)
            {
                await _segmentChange.RetryProjectionAsync(ProjectionScope.ForItem(excludedId), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Returns the IDs of the items recorded under the given season-state key whose
    /// automatic segments are withheld from Jellyfin. A key with no recorded
    /// disabled items yields an empty set rather than an error.
    /// </summary>
    /// <param name="seasonId">Season-state key (a movie's own ID for movies).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The disabled item IDs.</returns>
    [HttpGet("DisabledItems/{SeasonId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlySet<Guid>>> GetDisabledItems([FromRoute] Guid seasonId, CancellationToken cancellationToken = default)
    {
        return Ok(await _database.GetDisabledItemIdsAsync(seasonId, cancellationToken).ConfigureAwait(false));
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
        if (MediaItemHelper.FindSupported(itemId) is not { } item)
        {
            return NotFound();
        }

        // The row's season key is a server-side pruning detail; callers only name the
        // item. An episode Jellyfin resolved no season for reports Guid.Empty — fall
        // back to the item's own id (the movie convention) so the toggle keeps
        // working: cleanup prunes by item id, the key only serves the listing.
        var seasonKey = SeasonStateKeyResolver.Resolve(item);
        if (seasonKey == Guid.Empty)
        {
            seasonKey = itemId;
        }

        try
        {
            // The coordinator commits the flag durably with its projection work in one
            // transaction; a failed or skipped Jellyfin resync never rolls the flag
            // back — the journaled work converges the mirror instead.
            var outcome = await _segmentChange
                .ApplyAsync(new SegmentVisibilityChangeIntent(itemId, seasonKey, Visible: !disabled), cancellationToken)
                .ConfigureAwait(false);
            return SegmentChangeHttp.Map(
                outcome,
                onApplied: _ => NoContent(),
                // The flag already has the requested value; an idempotent toggle
                // succeeds (its journaled re-projection still heals a diverged mirror).
                onIgnored: _ => NoContent());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only a failure to commit throws; nothing was changed then.
            LogFailedToSetItemDisabled(_logger, ex, itemId, disabled);
            return Problem("Setting the item's disable flag failed; nothing was changed.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<IReadOnlyList<QueuedEpisode>> GetExcludedInventoryAsync(Plugin plugin, CancellationToken cancellationToken)
    {
        if (_libraryManager is not null)
        {
            var queueManager = _analyzerFactory.CreateQueueManager();
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
        return new ScanStatusResponse(ScanState.IsRunning(ScanState.FindDetectTask(_taskManager)));
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

                        var baseIntroAnalyzer = _analyzerFactory.CreateAnalyzerTask();

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to set item {ItemId} disabled={Disabled}; the authoritative write did not commit and nothing was changed")]
    private static partial void LogFailedToSetItemDisabled(ILogger logger, Exception ex, Guid itemId, bool disabled);
}
