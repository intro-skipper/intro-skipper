// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Per-item counts of Jellyfin media segment rows, split into rows owned by Intro
/// Skipper and rows owned by any other provider.
/// </summary>
/// <remarks>
/// Also the orphan listing's element: filtering the store's whole-table counts down to
/// items missing from the library changes which entries are present, not their shape.
/// </remarks>
/// <param name="ItemId">Item id.</param>
/// <param name="OwnCount">Number of Intro Skipper-owned rows.</param>
/// <param name="OtherCount">Number of rows owned by other providers.</param>
public sealed record ItemSegmentCounts(Guid ItemId, int OwnCount, int OtherCount);
