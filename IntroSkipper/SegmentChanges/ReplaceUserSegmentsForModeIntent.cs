// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.SegmentChanges;

/// <summary>Replaces active segments for one mode with user segments.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="Mode">Analysis mode.</param>
/// <param name="Segments">Complete user range set.</param>
public sealed record ReplaceUserSegmentsForModeIntent(Guid ItemId, AnalysisMode Mode, IReadOnlyList<SegmentRange> Segments) : SegmentChangeIntent(ItemId);
