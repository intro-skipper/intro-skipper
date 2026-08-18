// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.SegmentChanges;

/// <summary>An authoritative segment value affected by a change.</summary>
/// <param name="Id">Stable segment ID.</param>
/// <param name="ItemId">Item ID.</param>
/// <param name="Mode">Analysis mode.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
/// <param name="Source">Segment provenance.</param>
/// <param name="State">Segment lifecycle state.</param>
public sealed record SegmentValue(Guid Id, Guid ItemId, AnalysisMode Mode, long StartTicks, long EndTicks, SegmentSource Source, SegmentState State);
