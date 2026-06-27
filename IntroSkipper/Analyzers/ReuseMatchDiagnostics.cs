// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Analyzers;

/// <summary>
/// Cost diagnostics emitted alongside a <see cref="CrossEpisodeReuseMatcher"/> search, used to
/// substantiate the RFC's cost claims.
/// </summary>
/// <remarks>
/// RESEARCH SPIKE (RFC B) — see <c>docs/recap-research/B-cross-episode.md</c>.
/// </remarks>
/// <param name="DistinctShiftsDiscovered">Number of distinct candidate shifts found during voting.</param>
/// <param name="ShiftsScanned">Number of shifts actually scanned (bounded by <see cref="ReuseMatchOptions.TopShifts"/>).</param>
/// <param name="PointSetOverlap">Distinct-point overlap fraction used for early-exit.</param>
/// <param name="EarlyExit">Whether the pre-filter short-circuited the search.</param>
public readonly record struct ReuseMatchDiagnostics(
    int DistinctShiftsDiscovered,
    int ShiftsScanned,
    double PointSetOverlap,
    bool EarlyExit);
