// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Request body for updating a segment's boundaries via the plural segments API.
/// </summary>
/// <param name="Start">New start time in seconds.</param>
/// <param name="End">New end time in seconds; must be after <paramref name="Start"/>.</param>
public sealed record UpdateSegmentRequest(double Start, double End);
