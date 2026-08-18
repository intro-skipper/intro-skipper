// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>The intent already held and no transaction was needed.</summary>
/// <param name="Reason">Domain reason.</param>
public sealed record Ignored(string Reason) : SegmentChangeOutcome;
