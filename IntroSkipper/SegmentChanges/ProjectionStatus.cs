// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Aggregate projection status with per-item detail and counts.</summary>
/// <param name="Scope">Requested scope.</param>
/// <param name="Items">Per-item statuses.</param>
public sealed record ProjectionStatus(ProjectionScope Scope, IReadOnlyList<ItemProjectionStatus> Items)
{
    /// <summary>Gets the item count.</summary>
    public int ItemCount => Items.Count;

    /// <summary>Gets the number of applied items.</summary>
    public int AppliedCount => Items.Count(item => item.State == ProjectionState.Applied);

    /// <summary>Gets the number of pending items.</summary>
    public int PendingCount => Items.Count(item => item.State == ProjectionState.Pending);

    /// <summary>Gets the number of skipped items.</summary>
    public int SkippedCount => Items.Count(item => item.State == ProjectionState.Skipped);
}
