// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Current durable projection status for one item.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="LastAcceptedSequence">Last accepted sequence.</param>
/// <param name="LastAppliedSequence">Last sequence applied to Jellyfin.</param>
/// <param name="State">Current state.</param>
/// <param name="AttemptCount">Attempts for the earliest pending plan.</param>
/// <param name="NextAttemptAt">Next due time.</param>
/// <param name="Failure">Sanitized latest failure.</param>
public sealed record ItemProjectionStatus(Guid ItemId, long LastAcceptedSequence, long LastAppliedSequence, ProjectionState State, int AttemptCount, DateTime? NextAttemptAt, string? Failure);
