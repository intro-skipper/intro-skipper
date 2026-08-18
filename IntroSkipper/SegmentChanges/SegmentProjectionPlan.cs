// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>An immutable item projection plan passed to the adapter.</summary>
/// <param name="ChangeId">Change ID.</param>
/// <param name="ItemId">Item ID.</param>
/// <param name="Sequence">Per-item sequence.</param>
/// <param name="Segments">Complete own-row image.</param>
/// <param name="ExternalOperations">Ordered exact external operations.</param>
internal sealed record SegmentProjectionPlan(Guid ChangeId, Guid ItemId, long Sequence, IReadOnlyList<ProjectedSegment> Segments, IReadOnlyList<ProjectedExternalOperation> ExternalOperations);
