// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>The intent already held and no transaction was needed.</summary>
/// <param name="Reason">Typed no-change reason.</param>
/// <param name="Message">Human-readable reason.</param>
/// <param name="AffectedValues">Existing authoritative values relevant to the no-op.</param>
public sealed record Ignored(SegmentChangeIgnoredReason Reason, string Message, IReadOnlyList<SegmentValue> AffectedValues) : SegmentChangeOutcome
{
    /// <summary>Initializes a new instance of the <see cref="Ignored"/> class without a response value.</summary>
    /// <param name="reason">Typed no-change reason.</param>
    /// <param name="message">Human-readable reason.</param>
    public Ignored(SegmentChangeIgnoredReason reason, string message)
        : this(reason, message, [])
    {
    }
}
