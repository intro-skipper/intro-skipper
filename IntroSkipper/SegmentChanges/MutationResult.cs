// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.SegmentChanges;

/// <summary>Internal authoritative mutation result before plan creation.</summary>
/// <param name="Outcome">Ignored or rejected outcome, if no plan should be created.</param>
/// <param name="Affected">Affected authoritative segment values.</param>
/// <param name="ExternalOperations">Exact external operations to journal.</param>
internal sealed record MutationResult(SegmentChangeOutcome? Outcome, IReadOnlyList<SegmentValue> Affected, IReadOnlyList<ProjectedExternalOperation> ExternalOperations)
{
    internal static MutationResult Ignore(string reason) => new(new Ignored(reason), [], []);

    internal static MutationResult Reject(string reason) => new(new Rejected(reason), [], []);
}
