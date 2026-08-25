// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using IntroSkipper.SegmentChanges;
using Microsoft.AspNetCore.Mvc;

namespace IntroSkipper.Controllers;

internal static class SegmentChangeHttp
{
    internal static bool IsApplied(Accepted accepted)
        => accepted.Projections.All(projection => projection.State == ProjectionState.Applied);

    internal static AcceptedResult Accepted(Accepted accepted)
    {
        var projections = accepted.Projections.Select(projection => new SegmentProjectionAcceptedResponse(
            projection.ItemId,
            projection.State switch
            {
                ProjectionState.Applied => "Applied",
                ProjectionState.Pending => "Pending",
                ProjectionState.Skipped => "Skipped",
                _ => throw new ArgumentOutOfRangeException(nameof(accepted), projection.State, "Unknown projection state.")
            })).ToList();
        return new AcceptedResult((string?)null, new SegmentChangeAcceptedResponse(accepted.ChangeId, "Accepted", projections));
    }

    internal static ActionResult Rejected(Rejected rejected)
        => rejected.Reason switch
        {
            SegmentChangeRejectedReason.SegmentMissingOrSuppressed or
            SegmentChangeRejectedReason.ExternalSegmentNotFound or
            SegmentChangeRejectedReason.ExternalItemMismatch => new NotFoundResult(),
            SegmentChangeRejectedReason.ExternalIdMismatch or
            SegmentChangeRejectedReason.ExternalTypeMismatch => new BadRequestObjectResult(rejected.Message),
            _ => new BadRequestObjectResult(rejected.Message)
        };

    internal static SegmentDto ToDto(SegmentValue value) => new(
        value.Id,
        value.Mode,
        TickConversions.ToSeconds(value.StartTicks),
        TickConversions.ToSeconds(value.EndTicks),
        value.Source,
        value.State == SegmentState.Suppressed);
}
