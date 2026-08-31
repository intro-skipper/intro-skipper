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
    /// Maps a change outcome to HTTP in the one shape every mutation surface shares:
    /// a synchronously applied change and an ignored (already-held) intent keep the
    /// endpoint's established response via the given arms, a pending or skipped
    /// projection answers 202 through <see cref="Accepted"/>, and rejections map
    /// through <see cref="Rejected"/>.
    /// </summary>
    /// <param name="outcome">The typed change outcome.</param>
    /// <param name="onApplied">The endpoint's established success shape for a synchronously applied change.</param>
    /// <param name="onIgnored">The endpoint's shape for an intent that already held.</param>
    /// <returns>The mapped result.</returns>
    internal static ActionResult Map(SegmentChangeOutcome outcome, Func<Accepted, ActionResult> onApplied, Func<Ignored, ActionResult> onIgnored)
        => outcome switch
        {
            Accepted { Projection: ProjectionState.Applied } accepted => onApplied(accepted),
            Accepted accepted => Accepted(accepted),
            Ignored ignored => onIgnored(ignored),
            Rejected rejected => Rejected(rejected),
            _ => throw new InvalidOperationException($"Unknown segment change outcome '{outcome}'.")
        };

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
    /// target is 404 (the caller addressed something that does not exist for it —
    /// empty segment and item ids included, which the pre-cutover delete and update
    /// paths answered with 404 after their lookups found nothing), every other
    /// rejection is 400 with the typed message.
    /// </summary>
    /// <param name="rejected">The rejection outcome.</param>
    /// <returns>The mapped 404 or 400 result.</returns>
    internal static ActionResult Rejected(Rejected rejected) => rejected.Reason switch
    {
        SegmentChangeRejectedReason.SegmentMissingOrSuppressed or
        SegmentChangeRejectedReason.EmptyItemId or
        SegmentChangeRejectedReason.EmptySegmentId or
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
