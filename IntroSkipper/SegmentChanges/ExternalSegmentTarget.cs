// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>An exactly resolved Jellyfin segment.</summary>
/// <param name="Id">External row ID.</param>
/// <param name="ItemId">Owning item ID.</param>
/// <param name="Type">Jellyfin segment type.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
public sealed record ExternalSegmentTarget(Guid Id, Guid ItemId, MediaSegmentType Type, long StartTicks, long EndTicks);
