// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.SegmentChanges;

/// <summary>Base type of typed change outcomes.</summary>
public abstract record SegmentChangeOutcome;

/// <summary>The authoritative transaction committed and projection work was journaled.</summary>
/// <param name="AffectedValues">Affected authoritative segment values.</param>
/// <param name="Projection">Disposition of the immediate projection attempt.</param>
public sealed record Accepted(IReadOnlyList<SegmentValue> AffectedValues, ProjectionState Projection) : SegmentChangeOutcome;

/// <summary>
/// The intent already held: no mutation ran, but a re-projection was still journaled.
/// Re-asserting held state is how a diverged mirror heals on retry. The one exception:
/// an intent whose target exists in no state at all journals nothing, because nothing
/// addressable can have diverged.
/// </summary>
/// <param name="Reason">Typed no-change reason.</param>
/// <param name="Message">Human-readable reason.</param>
/// <param name="AffectedValues">The stored values that already satisfy the intent, when the reason has any (an already-existing user segment, say); empty otherwise.</param>
public sealed record Ignored(SegmentChangeIgnoredReason Reason, string Message, IReadOnlyList<SegmentValue> AffectedValues) : SegmentChangeOutcome;

/// <summary>The intent was invalid or did not own its addressed target.</summary>
/// <param name="Reason">Typed rejection reason.</param>
/// <param name="Message">Human-readable reason.</param>
public sealed record Rejected(SegmentChangeRejectedReason Reason, string Message) : SegmentChangeOutcome;

/// <summary>An authoritative segment value affected by a change.</summary>
/// <param name="Id">Stable segment ID.</param>
/// <param name="ItemId">Item ID.</param>
/// <param name="Mode">Analysis mode.</param>
/// <param name="StartTicks">Start ticks.</param>
/// <param name="EndTicks">End ticks.</param>
/// <param name="Source">Segment provenance.</param>
/// <param name="State">Segment lifecycle state.</param>
public sealed record SegmentValue(Guid Id, Guid ItemId, AnalysisMode Mode, long StartTicks, long EndTicks, SegmentSource Source, SegmentState State);

/// <summary>Authoritative mutation result of one applied change intent.</summary>
/// <param name="Outcome">Ignored or rejected outcome; <see langword="null"/> when the mutation committed.</param>
/// <param name="Affected">Affected authoritative segment values of a committed mutation.</param>
/// <param name="Reproject">Whether the change journals a re-projection. <see langword="false"/> only for
/// Ignored outcomes whose target exists in no state at all: nothing addressable can have diverged,
/// so a 404-style probe does not pay a journal write and a mirror sync.</param>
public sealed record MutationResult(SegmentChangeOutcome? Outcome, IReadOnlyList<SegmentValue> Affected, bool Reproject = true)
{
    internal static MutationResult Ignore(SegmentChangeIgnoredReason reason, string message, IReadOnlyList<SegmentValue>? affectedValues = null, bool reproject = true) => new(new Ignored(reason, message, affectedValues ?? []), [], reproject);

    internal static MutationResult Reject(SegmentChangeRejectedReason reason, string message) => new(new Rejected(reason, message), []);
}
