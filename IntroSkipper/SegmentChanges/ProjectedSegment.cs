// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;
using Jellyfin.Database.Implementations.Enums;

namespace IntroSkipper.SegmentChanges;

/// <summary>One complete Intro Skipper row image for Jellyfin.</summary>
/// <param name="Id">Stable segment ID.</param>
/// <param name="Type">Jellyfin segment type.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
/// <param name="Source">Segment provenance.</param>
internal sealed record ProjectedSegment(Guid Id, MediaSegmentType Type, long StartTicks, long EndTicks, SegmentSource Source);
