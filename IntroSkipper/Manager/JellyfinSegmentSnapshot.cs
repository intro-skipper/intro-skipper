// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Manager;

/// <summary>
/// A Jellyfin media segment row including its owning provider id, which
/// <see cref="MediaBrowser.Model.MediaSegments.MediaSegmentDto"/> does not carry.
/// </summary>
/// <param name="Id">Segment id.</param>
/// <param name="ItemId">Item id.</param>
/// <param name="Type">Segment type.</param>
/// <param name="StartTicks">Start position in ticks.</param>
/// <param name="EndTicks">End position in ticks.</param>
/// <param name="ProviderId">Owning provider id.</param>
public sealed record JellyfinSegmentSnapshot(
    Guid Id,
    Guid ItemId,
    MediaSegmentType Type,
    long StartTicks,
    long EndTicks,
    string ProviderId);
