// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Deletes one stored segment, preserving automatic intent as a tombstone.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="SegmentId">Segment ID.</param>
public sealed record DeleteSegmentIntent(Guid ItemId, Guid SegmentId) : SegmentChangeIntent(ItemId);
