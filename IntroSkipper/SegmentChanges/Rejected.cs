// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>The intent was invalid or did not own its addressed target.</summary>
/// <param name="Reason">Typed rejection reason.</param>
/// <param name="Message">Human-readable reason.</param>
public sealed record Rejected(SegmentChangeRejectedReason Reason, string Message) : SegmentChangeOutcome;
