// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Manager;

/// <summary>
/// Per-item counts of Jellyfin media segment rows, split into rows owned by Intro
/// Skipper and rows owned by any other provider.
/// </summary>
/// <param name="ItemId">Item id.</param>
/// <param name="OwnCount">Number of Intro Skipper-owned rows.</param>
/// <param name="OtherCount">Number of rows owned by other providers.</param>
public sealed record ItemSegmentCounts(Guid ItemId, int OwnCount, int OtherCount);
