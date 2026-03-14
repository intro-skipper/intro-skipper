// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// State of an episode.
/// </summary>
public enum EpisodeState
{
    /// <summary>
    /// Episode has not been analyzed.
    /// </summary>
    NotAnalyzed,

    /// <summary>
    /// Episode has been analyzed.
    /// </summary>
    Analyzed,

    /// <summary>
    /// Episode has been analyzed but no segments were found.
    /// </summary>
    NoSegments,
}
