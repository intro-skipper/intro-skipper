// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>One journaled foreign-row delete to apply.</summary>
/// <param name="ExternalSegmentId">External row ID.</param>
/// <param name="ExpectedType">The Jellyfin type the row carried when the delete was validated.</param>
/// <param name="StartTicks">The start ticks the row carried when the delete was validated.</param>
/// <param name="EndTicks">The end ticks the row carried when the delete was validated.</param>
internal sealed record ProjectedExternalOperation(Guid ExternalSegmentId, MediaSegmentType ExpectedType, long StartTicks, long EndTicks);
