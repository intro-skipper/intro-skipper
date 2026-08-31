// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>The authoritative transaction committed and projection work was journaled.</summary>
/// <param name="AffectedValues">Affected authoritative segment values.</param>
/// <param name="Projection">Disposition of the immediate projection attempt.</param>
public sealed record Accepted(IReadOnlyList<SegmentValue> AffectedValues, ProjectionState Projection) : SegmentChangeOutcome;
