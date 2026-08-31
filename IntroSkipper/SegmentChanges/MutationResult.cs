// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Authoritative mutation result of one applied change intent.</summary>
/// <param name="Outcome">Ignored or rejected outcome; <see langword="null"/> when the mutation committed.</param>
/// <param name="Affected">Affected authoritative segment values of a committed mutation.</param>
public sealed record MutationResult(SegmentChangeOutcome? Outcome, IReadOnlyList<SegmentValue> Affected)
{
    internal static MutationResult Ignore(SegmentChangeIgnoredReason reason, string message, IReadOnlyList<SegmentValue>? affectedValues = null) => new(new Ignored(reason, message, affectedValues ?? []), []);

    internal static MutationResult Reject(SegmentChangeRejectedReason reason, string message) => new(new Rejected(reason, message), []);
}
