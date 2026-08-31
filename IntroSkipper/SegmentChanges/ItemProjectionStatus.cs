// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Current durable projection status for one item.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="State">Current state.</param>
/// <param name="AttemptCount">Failed attempts of the pending work; 0 when nothing failed yet.</param>
/// <param name="NextAttemptAt">Next due time; <see langword="null"/> when due immediately or nothing is pending.</param>
/// <param name="Failure">Sanitized latest failure.</param>
public sealed record ItemProjectionStatus(Guid ItemId, ProjectionState State, int AttemptCount, DateTime? NextAttemptAt, string? Failure);
