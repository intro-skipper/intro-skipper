// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers;

/// <summary>
/// One reused span located by <see cref="CrossEpisodeReuseMatcher"/>: a contiguous block of
/// <see cref="QueryStart"/>..<see cref="QueryEnd"/> (inclusive point indices, in episode N) that matches
/// reference points starting at <see cref="ReferenceStart"/> (in the prior episode), at <see cref="Shift"/>.
/// </summary>
/// <remarks>
/// RESEARCH SPIKE (RFC B) — see <c>docs/recap-research/B-cross-episode.md</c>.
/// </remarks>
/// <param name="QueryStart">First matched point index in the query (episode N).</param>
/// <param name="QueryEnd">Last matched point index in the query (episode N).</param>
/// <param name="ReferenceStart">First matched point index in the reference (prior episode).</param>
/// <param name="Shift">Reference-minus-query index offset of this span.</param>
public readonly record struct ReusedSpan(int QueryStart, int QueryEnd, int ReferenceStart, int Shift)
{
    /// <summary>Gets the length of the span in fingerprint points.</summary>
    public int LengthPoints => QueryEnd - QueryStart + 1;

    /// <summary>Gets the start of the span in seconds (relative to the start of episode N).</summary>
    public double QueryStartSeconds => QueryStart * ChromaprintConstants.SampleDuration;

    /// <summary>Gets the end of the span in seconds (relative to the start of episode N).</summary>
    public double QueryEndSeconds => (QueryEnd + 1) * ChromaprintConstants.SampleDuration;
}
