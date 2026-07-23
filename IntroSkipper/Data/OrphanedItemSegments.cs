// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Jellyfin media segment rows belonging to an item that no longer exists in the library.
/// </summary>
/// <param name="ItemId">The orphaned item id.</param>
/// <param name="OwnCount">Number of Intro Skipper-owned rows.</param>
/// <param name="OtherCount">Number of rows owned by other providers.</param>
public sealed record OrphanedItemSegments(Guid ItemId, int OwnCount, int OtherCount);
