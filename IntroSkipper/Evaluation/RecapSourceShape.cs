// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Evaluation;

/// <summary>
/// Structural placement of the "Previously on…" recap within an episode.
/// Used to break down evaluation metrics per shape, because different shapes
/// stress different detection signals (e.g. a recap that does not start at 0 s
/// defeats the current "snap start to 0" black-frame fallback).
/// </summary>
internal enum RecapSourceShape
{
    /// <summary>
    /// The shape is unknown or has not been labeled yet.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The episode contains no recap. Paired with <c>HasRecap == false</c> labels and
    /// used to measure the false-positive rate.
    /// </summary>
    NoRecap = 1,

    /// <summary>
    /// The recap is the very first thing in the episode (starts at or near 0 s),
    /// before any cold open or intro.
    /// </summary>
    RecapFirst = 2,

    /// <summary>
    /// A cold open plays first, then the recap, then (usually) the intro.
    /// The recap does not start at 0 s.
    /// </summary>
    ColdOpenThenRecap = 3,

    /// <summary>
    /// The recap plays after the opening titles/intro. The current search window,
    /// which is capped at the detected intro start, structurally cannot reach this region.
    /// </summary>
    AfterIntro = 4,
}
