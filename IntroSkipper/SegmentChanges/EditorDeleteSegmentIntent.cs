// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// Deletes the editor-addressed segment the legacy <c>DELETE MediaSegmentsApi/{segmentId}</c>
/// wire contract names. A plugin row sharing the id is deleted authoritatively (tombstoning
/// automatic rows); an id with no plugin row falls back to the exactly validated external
/// delete of <see cref="DeleteExternalSegmentIntent"/>, uncorrelated counterpart matching
/// included.
/// </summary>
/// <param name="ItemId">Owning item ID.</param>
/// <param name="SegmentId">Editor-visible segment ID (shared with the plugin row when one exists).</param>
/// <param name="ExpectedType">The Jellyfin type the editor claims the segment has; a contradicting row is reported, not touched.</param>
public sealed record EditorDeleteSegmentIntent(Guid ItemId, Guid SegmentId, MediaSegmentType ExpectedType) : SegmentChangeIntent(ItemId);
