// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.SegmentChanges;

/// <summary>A user-provided range for one analysis mode.</summary>
/// <param name="Mode">Analysis mode.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
public sealed record UserTimestamp(AnalysisMode Mode, long StartTicks, long EndTicks);
