// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;
using IntroSkipper.Db;

namespace IntroSkipper.Data;

/// <summary>
/// One stored segment as exposed by the plural segments API
/// (<c>Episode/{itemId}/Segments</c>). Boundaries are seconds at the HTTP edge;
/// enums serialize as their names.
/// </summary>
/// <param name="Id">Segment id, shared with the Jellyfin media segment row on sync.</param>
/// <param name="Type">Analysis mode the segment belongs to.</param>
/// <param name="Start">Start time in seconds.</param>
/// <param name="End">End time in seconds.</param>
/// <param name="Source">Origin of the segment; <see cref="SegmentSource.User"/> marks user-provided segments.</param>
/// <param name="Suppressed">Whether the segment is a tombstone (user-deleted automatic segment).</param>
/// <param name="UpdatedAt">UTC time of the last modification.</param>
public sealed record SegmentDto(
    Guid Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter<AnalysisMode>))] AnalysisMode Type,
    double Start,
    double End,
    [property: JsonConverter(typeof(JsonStringEnumConverter<SegmentSource>))] SegmentSource Source,
    bool Suppressed,
    DateTime UpdatedAt)
{
    /// <summary>
    /// Converts a stored row to its API shape.
    /// </summary>
    /// <param name="segment">Stored segment row.</param>
    /// <returns>The API DTO.</returns>
    internal static SegmentDto FromDbSegment(DbSegment segment) => new(
        segment.Id,
        segment.Type,
        TickConversions.ToSeconds(segment.StartTicks),
        TickConversions.ToSeconds(segment.EndTicks),
        segment.Source,
        segment.State == SegmentState.Suppressed,
        segment.UpdatedAt);
}
