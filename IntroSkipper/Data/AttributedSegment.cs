// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Capy Agent
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A detected segment paired with the source that produced it, for analysis writes whose
/// segments do not all share one source.
/// </summary>
/// <param name="Segment">The segment in seconds.</param>
/// <param name="Source">The analyzer or combination that produced it; never <see cref="SegmentSource.User"/>.</param>
public readonly record struct AttributedSegment(Segment Segment, SegmentSource Source);
