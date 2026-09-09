// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Data;

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// What the black-frame analyzer found for one episode.
/// </summary>
/// <param name="Credits">The ranked and boundary-refined credits candidate in file time, or <see langword="null" /> when no accepted scene met the minimum duration.</param>
/// <param name="Scenes">Every black scene the black-frame rules accepted, relative to the credits fingerprint start. The analyzer returns one of them as <paramref name="Credits" />; the card analyzer treats card-like keyframes inside any of them as black cards.</param>
internal sealed record CreditsBlackFrameResult(Segment? Credits, IReadOnlyList<TimeRange> Scenes)
{
    /// <summary>
    /// Nothing accepted.
    /// </summary>
    public static readonly CreditsBlackFrameResult None = new(null, []);
}
