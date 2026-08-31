// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>One item's pending projection work.</summary>
/// <param name="Item">The untracked queue row.</param>
/// <param name="Operations">The journaled foreign-row deletes, in FIFO order.</param>
internal sealed record ProjectionWork(DbProjectionQueueItem Item, IReadOnlyList<DbProjectionExternalOperation> Operations);
