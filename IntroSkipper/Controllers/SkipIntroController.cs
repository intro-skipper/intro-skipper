// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2023 Péter Tombor
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 CasuallyFilthy
// SPDX-FileCopyrightText: 2024 Xameon42
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.Manager;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Skip intro controller.
/// </summary>
/// <param name="mediaSegmentRefresher">Media segment refresher.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="dbContextFactory">Factory for the segment database context.</param>
/// <param name="databaseInitializer">Database lifecycle owner (used for rebuild).</param>
/// <param name="logger">Logger.</param>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public class SkipIntroController(
    IMediaSegmentRefresher mediaSegmentRefresher,
    IDetectionCacheService cacheService,
    IDbContextFactory<IntroSkipperDbContext> dbContextFactory,
    DatabaseInitializer databaseInitializer,
    ILogger<SkipIntroController> logger) : ControllerBase
{
    private readonly IMediaSegmentRefresher _mediaSegmentRefresher = mediaSegmentRefresher;
    private readonly IDetectionCacheService _cacheService = cacheService;
    private readonly IDbContextFactory<IntroSkipperDbContext> _dbContextFactory = dbContextFactory;
    private readonly DatabaseInitializer _databaseInitializer = databaseInitializer;
    private readonly ILogger<SkipIntroController> _logger = logger;

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

        if (timestamps == null)
        {
            return NoContent();
        }

        var segmentTypes = new[]
        {
            (AnalysisMode.Introduction, timestamps.Introduction),
            (AnalysisMode.Credits, timestamps.Credits),
            (AnalysisMode.Recap, timestamps.Recap),
            (AnalysisMode.Preview, timestamps.Preview),
            (AnalysisMode.Commercial, timestamps.Commercial)
        };

        var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            foreach (var (mode, segment) in segmentTypes)
            {
                if (segment.Valid)
                {
                    segment.EpisodeId = id;
                    await db.UpdateTimestampAsync(segment, mode, isUserProvided: true, logger: _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (Plugin.Instance.Configuration.UpdateMediaSegments)
        {
            await _mediaSegmentRefresher.RefreshAsync(rawItem, cancellationToken).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Gets the timestamps for the provided episode.
    /// </summary>
    /// <param name="id">Episode ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Sucess.</response>
    /// <response code="404">Given ID is not an Episode.</response>
    /// <returns>Episode Timestamps.</returns>
    [HttpGet("Episode/{Id}/Timestamps")]
    [ActionName("UpdateTimestamps")]
    public async Task<ActionResult<TimeStamps>> GetTimestamps([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        // only get return content for episodes
        var rawItem = Plugin.Instance!.GetItem(id);
        if (rawItem is not Episode and not Movie)
        {
            return NotFound();
        }

        var times = new TimeStamps();
        var segments = await GetTimestampsFromDbAsync(id, cancellationToken).ConfigureAwait(false);

        if (segments.TryGetValue(AnalysisMode.Introduction, out var introSegment))
        {
            times.Introduction = introSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Credits, out var creditSegment))
        {
            times.Credits = creditSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Recap, out var recapSegment))
        {
            times.Recap = recapSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Preview, out var previewSegment))
        {
            times.Preview = previewSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Commercial, out var commercialSegment))
        {
            times.Commercial = commercialSegment;
        }

        return times;
    }

    /// <summary>
    /// Gets a dictionary of all skippable segments.
    /// </summary>
    /// <param name="id">Media ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Skippable segments dictionary.</response>
    /// <returns>Dictionary of skippable segments.</returns>
    [HttpGet("Episode/{id}/IntroSkipperSegments")]
    public async Task<ActionResult<Dictionary<AnalysisMode, Segment>>> GetSkippableSegments([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        var segments = await GetTimestampsFromDbAsync(id, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<AnalysisMode, Segment>();

        if (segments.TryGetValue(AnalysisMode.Introduction, out var introSegment))
        {
            result[AnalysisMode.Introduction] = introSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Credits, out var creditSegment))
        {
            result[AnalysisMode.Credits] = creditSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Recap, out var recapSegment))
        {
            result[AnalysisMode.Recap] = recapSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Preview, out var previewSegment))
        {
            result[AnalysisMode.Preview] = previewSegment;
        }

        if (segments.TryGetValue(AnalysisMode.Commercial, out var commercialSegment))
        {
            result[AnalysisMode.Commercial] = commercialSegment;
        }

        return result;
    }

    /// <summary>
    /// Erases all previously discovered introduction timestamps.
    /// </summary>
    /// <param name="mode">Mode.</param>
    /// <param name="eraseCache">Erase cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="204">Operation successful.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Intros/EraseTimestamps")]
    public async Task<ActionResult> ResetIntroTimestamps([FromQuery] AnalysisMode mode, [FromQuery] bool eraseCache = false, CancellationToken cancellationToken = default)
    {
        // Single-query call site: inline LINQ against the factory-created context is the
        // convention here; only segment writes must go through SegmentOperations.
        var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            await db.DbSegment
                .Where(s => s.Type == mode)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (eraseCache && mode is AnalysisMode.Introduction or AnalysisMode.Credits)
        {
            // Cache deletion must run to completion — the DB rows are already gone,
            // so aborting here would leave orphaned files with no way to clean them up.
            await Task.Run(() => _cacheService.DeleteByMode(mode), CancellationToken.None).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Rebuilds the database.
    /// </summary>
    /// <response code="204">Database rebuilt.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Intros/RebuildDatabase")]
    public async Task<ActionResult> RebuildDatabase()
    {
        // Database rebuild is destructive and must run to completion — do not bind to HttpContext.RequestAborted.
        await _databaseInitializer.RebuildDatabaseAsync().ConfigureAwait(false);
        return NoContent();
    }

    private async Task<IReadOnlyDictionary<AnalysisMode, Segment>> GetTimestampsFromDbAsync(Guid id, CancellationToken cancellationToken)
    {
        var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await db.GetTimestampsAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }
}
