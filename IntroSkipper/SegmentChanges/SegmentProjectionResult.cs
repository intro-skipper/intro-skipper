// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Projection disposition for one accepted item.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="State">Projection state.</param>
public sealed record SegmentProjectionResult(Guid ItemId, ProjectionState State);
