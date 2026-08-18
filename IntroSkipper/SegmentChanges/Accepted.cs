// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>The authoritative transaction committed and projection was journaled.</summary>
/// <param name="ChangeId">Durable change ID.</param>
/// <param name="AffectedValues">Affected authoritative segment values.</param>
/// <param name="Projections">Per-item projection disposition.</param>
public sealed record Accepted(Guid ChangeId, IReadOnlyList<SegmentValue> AffectedValues, IReadOnlyList<SegmentProjectionResult> Projections) : SegmentChangeOutcome;
