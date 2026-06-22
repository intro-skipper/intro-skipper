// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Response returned after clearing timestamp state for currently excluded media.
/// </summary>
/// <param name="AffectedItems">Number of excluded media items affected.</param>
/// <param name="RemovedSegments">Number of timestamp rows removed.</param>
/// <param name="RemovedCacheEntries">Number of detection cache rows removed.</param>
public sealed record ClearExcludedTimestampsResponse(int AffectedItems, int RemovedSegments, int RemovedCacheEntries);
