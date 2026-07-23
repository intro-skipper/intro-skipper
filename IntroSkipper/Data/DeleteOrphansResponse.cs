// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Result of an orphaned-segment cleanup.
/// </summary>
/// <param name="DeletedItemCount">Number of orphaned items whose Intro Skipper rows were deleted.</param>
public sealed record DeleteOrphansResponse(int DeletedItemCount);
