// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>Deletes an editor-addressed segment using shared-id or exact external fallback semantics.</summary>
/// <param name="ItemId">Owning item ID.</param>
/// <param name="SegmentId">Editor-visible segment ID.</param>
/// <param name="ExpectedType">Type supplied by the editor.</param>
public sealed record EditorDeleteSegmentIntent(Guid ItemId, Guid SegmentId, MediaSegmentType ExpectedType) : SegmentChangeIntent(ItemId);
