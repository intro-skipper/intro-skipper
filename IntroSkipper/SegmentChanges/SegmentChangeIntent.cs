// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Base type of every closed authoritative segment mutation.</summary>
/// <param name="ItemId">Item ID.</param>
public abstract record SegmentChangeIntent(Guid ItemId);
