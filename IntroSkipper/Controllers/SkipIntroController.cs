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
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.SegmentChanges;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

/// <summary>
/// Skip intro controller.
/// </summary>
[Authorize]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
public partial class SkipIntroController(
    ISegmentChange segmentChange,
    IDetectionCacheDatabase cacheDatabase,
    IIntroSkipperDatabase database) : ControllerBase
{
    private readonly ISegmentChange _segmentChange = segmentChange;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly IIntroSkipperDatabase _database = database;

    /// <summary>
    /// Updates the timestamps for the provided episode.
    /// </summary>
    /// <remarks>
    /// Deprecated: use the plural <c>Episode/{itemId}/Segments</c> API. Each provided slot
    /// replaces every stored segment of its mode with the single user segment; all slots
    /// commit in one durable transaction. A change whose Jellyfin projection does not
    /// apply synchronously answers <c>202 Accepted</c> and converges from the journal.
    /// </remarks>
    /// <param name="id">Episode ID to update timestamps for.</param>
    /// <param name="timestamps">New timestamps Introduction/Credits start and end times.</param>
    /// <param name="cancellationToken">Cancellation Token.</param>
    /// <response code="204">New timestamps saved.</response>
    /// <response code="202">The timestamps committed but their Jellyfin projection is pending or skipped.</response>
    /// <response code="404">Given ID is not an Episode.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Episode/{Id}/Timestamps")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateTimestampsAsync([FromRoute] Guid id, [FromBody] TimeStamps timestamps, CancellationToken cancellationToken = default)
    {
        // only update existing episodes
        if (MediaItemHelper.FindSupported(id) is null)
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

        var slots = new List<UserTimestamp>();
        foreach (var (mode, segment) in segmentTypes)
        {
            // An empty or degenerate slot (end not after start) is skipped, not stored.
            if (TickConversions.TryFromSecondsRange(segment.Start, segment.End, out var startTicks, out var endTicks))
            {
                slots.Add(new UserTimestamp(mode, startTicks, endTicks));
            }
        }

        if (slots.Count == 0)
        {
            // Nothing to store is a pure no-op (release-note item): the old path ran
            // an empty replace plus a mirror sync here, which incidentally healed a
            // diverged mirror; healing now rides on real mutations and the journal.
            return NoContent();
        }

        var outcome = await _segmentChange.ApplyAsync(new WriteUserTimestampsIntent(id, slots), cancellationToken).ConfigureAwait(false);
        return SegmentChangeHttp.Map(
            outcome,
            onApplied: _ => NoContent(),
            // The requested timestamps are already stored; an idempotent re-POST succeeds.
            onIgnored: _ => NoContent());
    }

    /// <summary>
    /// Gets the timestamps for the provided episode.
    /// </summary>
    /// <remarks>
    /// Deprecated: playback clients should use Jellyfin's native <c>MediaSegments</c>
    /// API, which the plugin keeps in sync. Reports one canonical segment per mode
    /// (the active segment with the earliest start); a disabled item reports only its
    /// user-provided segments.
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
        if (MediaItemHelper.FindSupported(id) is null)
        {
            return NotFound();
        }

        var segments = LegacyTimestampMapper.ToCanonical(
            await _database.GetServableSegmentsAsync(id, cancellationToken).ConfigureAwait(false));

        // Missing modes serialize as the empty segment, as the old per-mode assignments did.
        return new TimeStamps
        {
            Introduction = segments.GetValueOrDefault(AnalysisMode.Introduction) ?? new Segment(),
            Credits = segments.GetValueOrDefault(AnalysisMode.Credits) ?? new Segment(),
            Recap = segments.GetValueOrDefault(AnalysisMode.Recap) ?? new Segment(),
            Preview = segments.GetValueOrDefault(AnalysisMode.Preview) ?? new Segment(),
            Commercial = segments.GetValueOrDefault(AnalysisMode.Commercial) ?? new Segment(),
        };
    }

    /// <summary>
    /// Gets a dictionary of all skippable segments.
    /// </summary>
    /// <remarks>
    /// Deprecated: playback clients should use Jellyfin's native <c>MediaSegments</c>
    /// API, which the plugin keeps in sync. Reports one canonical segment per mode
    /// (the active segment with the earliest start); a disabled item reports only its
    /// user-provided segments.
    /// </remarks>
    /// <param name="id">Media ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Skippable segments dictionary.</response>
    /// <returns>Dictionary of skippable segments.</returns>
    [HttpGet("Episode/{id}/IntroSkipperSegments")]
    public async Task<ActionResult<Dictionary<AnalysisMode, Segment>>> GetSkippableSegments([FromRoute] Guid id, CancellationToken cancellationToken = default)
    {
        return LegacyTimestampMapper.ToCanonical(
            await _database.GetServableSegmentsAsync(id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Erases all previously discovered timestamps of a mode: the stored segments
    /// (tombstones included), the mode's analyzed-episode lists so the next scan
    /// re-detects, and the affected items' Jellyfin mirrors.
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
        var itemIds = await _database.DeleteSegmentsByModeAsync(mode, cancellationToken).ConfigureAwait(false);

        if (eraseCache && mode is AnalysisMode.Introduction or AnalysisMode.Credits)
        {
            // Best-effort cache cleanup (the facade logs and swallows database errors),
            // run off the request thread and not bound to request cancellation: the main
            // database rows are already gone, so make one complete cleanup attempt.
            await Task.Run(() => _cacheDatabase.DeleteByMode(mode), CancellationToken.None).ConfigureAwait(false);
        }

        // The erase journaled every affected item's projection; converge exactly those
        // items now for a snappy dashboard — unrelated pending work keeps its backoff.
        // Anything this pass cannot finish stays journaled and the worker completes it.
        await _segmentChange.ProjectItemsAsync(itemIds, cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Rebuilds the database.
    /// </summary>
    /// <param name="forceCleanOnBackupFailure">
    /// When <c>true</c>, the rebuild proceeds with an empty database if the existing one
    /// cannot be read for backup — every stored timestamp is discarded. When <c>false</c>,
    /// such a rebuild aborts with 409 so no data is lost without explicit consent.
    /// </param>
    /// <response code="204">Database rebuilt.</response>
    /// <response code="409">The existing database could not be read for backup; repeat with forceCleanOnBackupFailure=true to discard it and rebuild empty.</response>
    /// <returns>No content.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Intros/RebuildDatabase")]
    public async Task<ActionResult> RebuildDatabase([FromQuery] bool forceCleanOnBackupFailure = false)
    {
        try
        {
            // Database rebuild is destructive and must run to completion — do not bind to HttpContext.RequestAborted.
            await _database.RebuildDatabaseAsync(forceCleanOnBackupFailure).ConfigureAwait(false);
        }
        catch (DatabaseRebuildBackupException)
        {
            return Conflict(new { message = "The existing database could not be read for backup. Repeat the request with forceCleanOnBackupFailure=true to discard it and rebuild empty." });
        }

        return NoContent();
    }
}
