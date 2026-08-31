// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>Deletes one exactly validated Jellyfin segment and any exact counterpart.</summary>
/// <param name="ItemId">Expected owning item ID.</param>
/// <param name="ExternalSegmentId">External row ID.</param>
/// <param name="ExpectedType">Expected Jellyfin type.</param>
public sealed record DeleteExternalSegmentIntent(Guid ItemId, Guid ExternalSegmentId, MediaSegmentType ExpectedType) : SegmentChangeIntent(ItemId);
