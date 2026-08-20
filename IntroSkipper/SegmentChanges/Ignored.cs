// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>The intent already held and no transaction was needed.</summary>
/// <param name="Reason">Typed no-change reason.</param>
/// <param name="Message">Human-readable reason.</param>
public sealed record Ignored(SegmentChangeIgnoredReason Reason, string Message) : SegmentChangeOutcome;
