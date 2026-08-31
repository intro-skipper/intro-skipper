// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// The intent already held: no mutation ran, but a re-projection was still journaled —
/// re-asserting held state is how a diverged mirror heals on retry.
/// </summary>
/// <param name="Reason">Typed no-change reason.</param>
/// <param name="Message">Human-readable reason.</param>
public sealed record Ignored(SegmentChangeIgnoredReason Reason, string Message) : SegmentChangeOutcome;
