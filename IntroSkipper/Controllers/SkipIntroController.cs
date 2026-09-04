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
using IntroSkipper.SegmentChanges;
using MediaBrowser.Common.Api;
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
    SegmentChange segmentChange,
    IDetectionCacheDatabase cacheDatabase,
    IIntroSkipperDatabase database) : ControllerBase
{
    private readonly SegmentChange _segmentChange = segmentChange;
    private readonly IDetectionCacheDatabase _cacheDatabase = cacheDatabase;
    private readonly IIntroSkipperDatabase _database = database;

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
            // not bound to request cancellation: the main database rows are already
            // gone, so make one complete cleanup attempt.
            await _cacheDatabase.DeleteByModeAsync(mode, CancellationToken.None).ConfigureAwait(false);
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
