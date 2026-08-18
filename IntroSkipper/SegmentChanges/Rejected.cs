// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>The intent was invalid or did not own its addressed target.</summary>
/// <param name="Reason">Domain reason.</param>
public sealed record Rejected(string Reason) : SegmentChangeOutcome;
