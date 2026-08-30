// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Db;

namespace IntroSkipper.Data;

/// <summary>
/// Collapse rule for the deprecated singular timestamp endpoints
/// (<c>Episode/{id}/Timestamps</c>, <c>Episode/{id}/IntroSkipperSegments</c>): one
/// canonical segment per analysis mode, the active segment with the earliest start.
/// Kept byte-compatible with the pre-plural wire behavior; nothing else may use it.
/// </summary>
internal static class LegacyTimestampMapper
{
    /// <summary>
    /// Reduces stored segments to one canonical timestamp per mode. This is the owner of
    /// the tombstone filter for these endpoints: it accepts raw stored rows, so callers
    /// reading through a pre-filtered query must not drop it on that assumption.
    /// </summary>
    /// <param name="segments">Stored segments of a single item, suppressed rows included.</param>
    /// <returns>The canonical timestamp per analysis mode.</returns>
    internal static Dictionary<AnalysisMode, Segment> ToCanonical(IEnumerable<DbSegment> segments)
        => segments
            .Where(s => s.State == SegmentState.Active)
            .GroupBy(s => s.Type)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(s => s.StartTicks).First().ToSegment());
}
