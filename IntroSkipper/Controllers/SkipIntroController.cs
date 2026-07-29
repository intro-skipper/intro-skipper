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
public partial class SkipIntroController(
    IMediaSegmentRefresher mediaSegmentRefresher,
    IDetectionCacheDatabase cacheDatabase,
    IIntroSkipperDatabase database) : ControllerBase
{
    private readonly IMediaSegmentRefresher _mediaSegmentRefresher = mediaSegmentRefresher;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly IIntroSkipperDatabase _database = database;

    /// <summary>
    /// Updates the timestamps for the provided episode.
    /// </summary>
    /// <remarks>
    /// Deprecated: use the plural <c>Episode/{itemId}/Segments</c> API. Each provided slot
    /// replaces every stored segment of its mode with the single user segment.
    /// </remarks>
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

        foreach (var (mode, segment) in segmentTypes)
        {
            if (segment.Valid
                && TickConversions.TryFromSeconds(segment.Start, out var startTicks)
                && TickConversions.TryFromSeconds(segment.End, out var endTicks)
                && endTicks > startTicks)
            {
                await _database.ReplaceUserSegmentAsync(id, mode, startTicks, endTicks, cancellationToken).ConfigureAwait(false);
            }
        }

        await _mediaSegmentRefresher.RefreshAsync(rawItem, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Gets the timestamps for the provided episode.
    /// </summary>
    /// <remarks>
    /// Deprecated: use the plural <c>Episode/{itemId}/Segments</c> API. Reports one
    /// canonical segment per mode (the active segment with the earliest start).
    /// </remarks>
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
        var segments = LegacyTimestampMapper.ToCanonical(
            await _database.GetSegmentsAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false));

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
    /// <remarks>
    /// Deprecated: use the plural <c>Episode/{itemId}/Segments</c> API. Reports one
    /// canonical segment per mode (the active segment with the earliest start).
    /// </remarks>
    /// <param name="id">Media ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Skippable segments dictionary.</response>
    /// <returns>Dictionary of skippable segments.</returns>
    [HttpGet("Episode/{id}/IntroSkipperSegments")]
    public async Task<ActionResult<Dictionary<AnalysisMode, Segment>>> GetSkippableSegments([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        var segments = LegacyTimestampMapper.ToCanonical(
            await _database.GetSegmentsAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false));
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
        await _database.DeleteSegmentsByModeAsync(mode, cancellationToken).ConfigureAwait(false);

        if (eraseCache && mode is AnalysisMode.Introduction or AnalysisMode.Credits)
        {
            // Best-effort cache cleanup (the facade logs and swallows database errors),
            // run off the request thread and not bound to request cancellation: the main
            // database rows are already gone, so make one complete cleanup attempt.
            await Task.Run(() => _cacheDatabase.DeleteByMode(mode), CancellationToken.None).ConfigureAwait(false);
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
        await _database.RebuildDatabaseAsync().ConfigureAwait(false);
        return NoContent();
    }
}
