// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Boundary-snapping data assembled from the detection cache: positions in absolute
/// seconds that segment edges can snap to. Arrays are empty when no cached data exists;
/// black intervals are additionally omitted when their scan anchor cannot be recovered.
/// </summary>
/// <param name="Keyframes">Keyframe positions, sorted ascending.</param>
/// <param name="BlackIntervals">Detected black intervals.</param>
/// <param name="Silence">Detected silence ranges.</param>
/// <param name="FromCache">Whether any cached detection data existed for the item.</param>
public sealed record SnapPointsResponse(
    IReadOnlyList<double> Keyframes,
    IReadOnlyList<SnapRange> BlackIntervals,
    IReadOnlyList<SnapRange> Silence,
    bool FromCache);
