// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>Wire projection status for one accepted item.</summary>
/// <param name="ItemId">Projected item ID.</param>
/// <param name="Status">Stable projection status token.</param>
public sealed record SegmentProjectionAcceptedResponse(Guid ItemId, string Status);
