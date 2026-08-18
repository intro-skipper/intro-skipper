// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>One ordered exact external operation.</summary>
/// <param name="ExternalSegmentId">External row ID.</param>
/// <param name="ExpectedType">Validated Jellyfin type.</param>
/// <param name="Kind">Operation kind.</param>
internal sealed record ProjectedExternalOperation(Guid ExternalSegmentId, MediaSegmentType ExpectedType, ProjectionExternalOperationKind Kind);
