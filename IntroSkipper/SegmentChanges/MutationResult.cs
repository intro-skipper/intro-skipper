// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Authoritative mutation result of one applied change intent.</summary>
/// <param name="Outcome">Ignored or rejected outcome; <see langword="null"/> when the mutation committed.</param>
/// <param name="Affected">Affected authoritative segment values of a committed mutation.</param>
/// <param name="Reproject">Whether the change journals a re-projection. <see langword="false"/> only for
/// Ignored outcomes whose target exists in no state at all — nothing addressable can have diverged,
/// so a 404-style probe does not pay a journal write and a mirror sync.</param>
public sealed record MutationResult(SegmentChangeOutcome? Outcome, IReadOnlyList<SegmentValue> Affected, bool Reproject = true)
{
    internal static MutationResult Ignore(SegmentChangeIgnoredReason reason, string message, IReadOnlyList<SegmentValue>? affectedValues = null, bool reproject = true) => new(new Ignored(reason, message, affectedValues ?? []), [], reproject);

    internal static MutationResult Reject(SegmentChangeRejectedReason reason, string message) => new(new Rejected(reason, message), []);
}
