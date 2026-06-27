// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Subtitles;

/// <summary>
/// The result of building a recap segment from subtitle cues.
/// </summary>
/// <param name="Start">Recap start time (in seconds).</param>
/// <param name="End">Recap end time (in seconds).</param>
/// <param name="MatchedPhrase">The normalized recap-opening phrase that anchored the segment.</param>
/// <param name="AnchorCueText">The raw text of the cue that anchored the segment.</param>
/// <param name="CueCount">Number of cues absorbed into the recap cluster.</param>
/// <param name="SnappedToBlackFrame">Whether <see cref="End"/> was snapped to a black frame.</param>
public record SubtitleRecapResult(
    double Start,
    double End,
    string MatchedPhrase,
    string AnchorCueText,
    int CueCount,
    bool SnappedToBlackFrame)
{
    /// <summary>
    /// Gets the recap duration (in seconds).
    /// </summary>
    public double Duration => End - Start;
}
