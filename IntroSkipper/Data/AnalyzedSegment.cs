// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace IntroSkipper.Data;

/// <summary>
/// Represents an analyzed segment with metadata for batch processing.
/// </summary>
/// <param name="Segment">The detected segment.</param>
/// <param name="IsFirstAppearance">Whether this is the first episode where this intro pattern was detected.</param>
public record AnalyzedSegment(Segment Segment, bool IsFirstAppearance = false);
