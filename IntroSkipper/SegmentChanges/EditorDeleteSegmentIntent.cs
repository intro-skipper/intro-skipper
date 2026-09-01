// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Deletes one editor-addressed segment, the single external-delete intent (the
/// legacy <c>DELETE MediaSegmentsApi/{segmentId}</c> wire contract). A plugin row
/// sharing the id is deleted authoritatively (tombstoning automatic rows) with its
/// Jellyfin twin's targeted delete journaled; an id with no plugin row resolves the
/// Jellyfin row lazily inside the transaction, validates its ownership, matches the
/// uncorrelated plugin counterpart, and journals the foreign row's durable delete.
/// </summary>
/// <param name="ItemId">Owning item ID.</param>
/// <param name="SegmentId">Editor-visible segment ID (shared with the plugin row when one exists).</param>
/// <param name="ExpectedType">The Jellyfin type the editor claims the segment has; a contradicting row is reported, not touched.</param>
public sealed record EditorDeleteSegmentIntent(Guid ItemId, Guid SegmentId, MediaSegmentType ExpectedType) : SegmentChangeIntent(ItemId);
