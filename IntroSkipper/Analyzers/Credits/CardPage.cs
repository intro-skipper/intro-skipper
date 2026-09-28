// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Analyzers.Credits;

/// <summary>
/// What a page is to the card run. The keyframe analyzer sets it after probing, from the black scenes
/// the black-frame rules accepted and rejected (see <see cref="KeyframeAnalyzer.StampCardKinds"/>).
/// </summary>
internal enum CardKind
{
    /// <summary>
    /// Not a card: counts against a run's density, and splits a run between two pages more than the
    /// bridge apart.
    /// </summary>
    Content,

    /// <summary>
    /// A credit card: extends a run and counts toward its density.
    /// </summary>
    Card,

    /// <summary>
    /// A black or card-like page inside an accepted black scene: extends a run and counts toward its
    /// duration, never its density.
    /// </summary>
    BlackCard,
}

/// <summary>
/// A page as the card run finder reads it.
/// </summary>
/// <param name="Time">The page's black-frame time, relative to the credits fingerprint start.</param>
/// <param name="Kind">The page's card kind.</param>
internal readonly record struct CardPage(double Time, CardKind Kind);
