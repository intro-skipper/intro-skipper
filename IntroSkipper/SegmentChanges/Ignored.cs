// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>
/// The intent already held: no mutation ran, but a re-projection was still journaled —
/// re-asserting held state is how a diverged mirror heals on retry. The one exception:
/// an intent whose target exists in no state at all journals nothing, because nothing
/// addressable can have diverged.
/// </summary>
/// <param name="Reason">Typed no-change reason.</param>
/// <param name="Message">Human-readable reason.</param>
/// <param name="AffectedValues">The stored values that already satisfy the intent, when the reason has any (an already-existing user segment, say); empty otherwise.</param>
public sealed record Ignored(SegmentChangeIgnoredReason Reason, string Message, IReadOnlyList<SegmentValue> AffectedValues) : SegmentChangeOutcome;
