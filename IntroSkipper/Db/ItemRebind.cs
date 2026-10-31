// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>
/// One orphan whose rows move to the live item with the same file hash.
/// </summary>
/// <param name="Orphan">The orphan's file hash row: its id, season key and the file version its hash was computed at.</param>
/// <param name="ItemId">The live item that takes over the rows.</param>
/// <param name="FileVersion">The live item's file version, stamped on the analysis records that carried the orphan's.</param>
public readonly record struct ItemRebind(DbFileHash Orphan, Guid ItemId, long? FileVersion);
