// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.SegmentChanges;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

/// <summary>
/// Shared mapping from typed segment-change outcomes to HTTP results, so every
/// mutation surface reports the same accepted-but-pending and rejection semantics.
/// </summary>
internal static class SegmentChangeHttp
{
    /// <summary>
    /// Builds the <c>202 Accepted</c> result for a committed change whose projection
    /// is pending or intentionally skipped; the synchronous-applied case keeps each
    /// endpoint's established response shape instead.
    /// </summary>
    /// <param name="accepted">The committed outcome.</param>
    /// <returns>The 202 result carrying a <see cref="SegmentChangeAcceptedResponse"/>.</returns>
    internal static AcceptedResult Accepted(Accepted accepted) => new(
        (string?)null,
        new SegmentChangeAcceptedResponse(
            "Accepted",
            accepted.Projection == ProjectionState.Skipped ? "Skipped" : "Pending",
            accepted.AffectedValues.Select(ToDto).ToList()));

    /// <summary>
    /// Maps a rejection to its established wire status: an absent or foreign-owned
    /// target is 404 (the caller addressed something that does not exist for it),
    /// every other rejection is 400 with the typed message.
    /// </summary>
    /// <param name="rejected">The rejection outcome.</param>
    /// <returns>The mapped 404 or 400 result.</returns>
    internal static ActionResult Rejected(Rejected rejected) => rejected.Reason switch
    {
        SegmentChangeRejectedReason.SegmentMissingOrSuppressed or
        SegmentChangeRejectedReason.ExternalSegmentNotFound or
        SegmentChangeRejectedReason.ExternalItemMismatch => new NotFoundResult(),
        _ => new BadRequestObjectResult(rejected.Message)
    };

    /// <summary>Converts a committed segment value to the plural API's DTO shape.</summary>
    /// <param name="value">The committed segment value.</param>
    /// <returns>The API DTO.</returns>
    internal static SegmentDto ToDto(SegmentValue value) => new(
        value.Id,
        value.Mode,
        TickConversions.ToSeconds(value.StartTicks),
        TickConversions.ToSeconds(value.EndTicks),
        value.Source,
        value.State == SegmentState.Suppressed);
}
