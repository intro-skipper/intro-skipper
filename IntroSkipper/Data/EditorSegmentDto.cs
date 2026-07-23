// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.Data;

/// <summary>
/// A Jellyfin media segment annotated with editor metadata: the owning provider and, for
/// Intro Skipper's own rows, whether the plugin-side counterpart was user-provided.
/// Property names serialize as-is (PascalCase) so the wire shape is a strict superset of
/// Jellyfin's <see cref="MediaBrowser.Model.MediaSegments.MediaSegmentDto"/>.
/// </summary>
/// <param name="Id">Segment id.</param>
/// <param name="ItemId">Item id.</param>
/// <param name="Type">Segment type.</param>
/// <param name="StartTicks">Start position in ticks.</param>
/// <param name="EndTicks">End position in ticks.</param>
/// <param name="ProviderId">Owning provider id.</param>
/// <param name="ProviderName">Owning provider display name, when a registered provider matches <paramref name="ProviderId"/>.</param>
/// <param name="IsUserProvided">Whether Intro Skipper's plugin-side counterpart is user-provided; null for other providers' rows or when no counterpart exists.</param>
public sealed record EditorSegmentDto(
    Guid Id,
    Guid ItemId,
    MediaSegmentType Type,
    long StartTicks,
    long EndTicks,
    string ProviderId,
    string? ProviderName,
    bool? IsUserProvided);
