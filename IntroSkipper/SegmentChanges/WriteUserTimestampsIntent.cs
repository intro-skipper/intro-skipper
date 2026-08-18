// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Atomically writes user timestamps and analyzed state for several modes.</summary>
/// <param name="ItemId">Item ID.</param>
/// <param name="Timestamps">Unique mode timestamps.</param>
public sealed record WriteUserTimestampsIntent(Guid ItemId, IReadOnlyList<UserTimestamp> Timestamps) : SegmentChangeIntent(ItemId);
