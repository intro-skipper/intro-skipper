// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Request body for updating a segment's type and boundaries via the plural segments API.
/// </summary>
/// <param name="Type">New segment type; omitted to preserve the current type for older clients.</param>
/// <param name="Start">New start time in seconds.</param>
/// <param name="End">New end time in seconds; must be after <paramref name="Start"/>.</param>
public sealed record UpdateSegmentRequest(double Start, double End, AnalysisMode? Type = null);
