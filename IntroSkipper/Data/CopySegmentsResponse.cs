// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Result of a copy operation, one entry per target item.
/// </summary>
/// <param name="Results">Per-target outcomes.</param>
public sealed record CopySegmentsResponse(IReadOnlyList<CopyItemResult> Results);
