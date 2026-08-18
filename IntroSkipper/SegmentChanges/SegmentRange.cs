// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>An immutable tick range.</summary>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
public sealed record SegmentRange(long StartTicks, long EndTicks);
