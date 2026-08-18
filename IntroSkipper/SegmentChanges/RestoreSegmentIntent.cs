// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Restores one automatic tombstone.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="SegmentId">Segment ID.</param>
public sealed record RestoreSegmentIntent(Guid ItemId, Guid SegmentId) : SegmentChangeIntent(ItemId);
