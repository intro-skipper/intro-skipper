// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>Base type of every closed authoritative segment mutation.</summary>
/// <param name="ItemId">Item ID.</param>
public abstract record SegmentChangeIntent(Guid ItemId);

/// <summary>Adds or promotes one user segment.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="Mode">Analysis mode.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
public sealed record AddUserSegmentIntent(Guid ItemId, AnalysisMode Mode, long StartTicks, long EndTicks) : SegmentChangeIntent(ItemId);

/// <summary>Replaces active segments for one mode with user segments.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="Mode">Analysis mode.</param>
/// <param name="Segments">Complete user range set.</param>
public sealed record ReplaceUserSegmentsForModeIntent(Guid ItemId, AnalysisMode Mode, IReadOnlyList<SegmentRange> Segments) : SegmentChangeIntent(ItemId);

/// <summary>Updates one segment and promotes the surviving row to user provenance.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="SegmentId">Segment ID.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
public sealed record UpdateSegmentIntent(Guid ItemId, Guid SegmentId, long StartTicks, long EndTicks) : SegmentChangeIntent(ItemId);

/// <summary>Deletes one stored segment, preserving automatic intent as a tombstone.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="SegmentId">Segment ID.</param>
public sealed record DeleteSegmentIntent(Guid ItemId, Guid SegmentId) : SegmentChangeIntent(ItemId);

/// <summary>Restores one automatic tombstone.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="SegmentId">Segment ID.</param>
public sealed record RestoreSegmentIntent(Guid ItemId, Guid SegmentId) : SegmentChangeIntent(ItemId);

/// <summary>Changes whether automatic segments are visible for one item.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="SeasonId">Owning season-state key.</param>
/// <param name="Visible">Whether automatic output is visible.</param>
public sealed record SegmentVisibilityChangeIntent(Guid ItemId, Guid SeasonId, bool Visible) : SegmentChangeIntent(ItemId);

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

/// <summary>An immutable tick range.</summary>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
public sealed record SegmentRange(long StartTicks, long EndTicks);
