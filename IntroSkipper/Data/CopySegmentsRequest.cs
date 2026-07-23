// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Data;

/// <summary>
/// Request to copy one item's segments to other items.
/// </summary>
/// <param name="SourceItemId">The item whose segments are copied.</param>
/// <param name="TargetItemIds">The items the segments are applied to.</param>
/// <param name="Types">The segment types to copy; null copies every editor-managed type. Only types the source actually carries are written — requested types the source lacks are left untouched on the targets, because the copy replaces the types it writes.</param>
/// <param name="TimeShiftTicks">Optional shift applied to every copied segment, in ticks; may be negative.</param>
public sealed record CopySegmentsRequest(
    Guid SourceItemId,
    IReadOnlyList<Guid> TargetItemIds,
    IReadOnlyList<MediaSegmentType>? Types = null,
    long TimeShiftTicks = 0);
