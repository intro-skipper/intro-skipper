// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Updates one segment and promotes the surviving row to user provenance.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="SegmentId">Segment ID.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
public sealed record UpdateSegmentIntent(Guid ItemId, Guid SegmentId, long StartTicks, long EndTicks) : SegmentChangeIntent(ItemId);
