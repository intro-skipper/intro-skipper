// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.SegmentChanges;

/// <summary>Adds or promotes one user segment.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="Mode">Analysis mode.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
public sealed record AddUserSegmentIntent(Guid ItemId, AnalysisMode Mode, long StartTicks, long EndTicks) : SegmentChangeIntent(ItemId);
