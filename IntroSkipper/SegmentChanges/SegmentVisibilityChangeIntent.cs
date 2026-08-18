// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Changes whether automatic segments are visible for one item.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="SeasonId">Owning season-state key.</param>
/// <param name="Visible">Whether automatic output is visible.</param>
public sealed record SegmentVisibilityChangeIntent(Guid ItemId, Guid SeasonId, bool Visible) : SegmentChangeIntent(ItemId);
