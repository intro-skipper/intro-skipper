// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Subtitles;

/// <summary>
/// A single timed subtitle cue parsed from a text subtitle stream or sidecar file.
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
/// <param name="Start">Cue start time (in seconds).</param>
/// <param name="End">Cue end time (in seconds).</param>
/// <param name="Text">Plain cue text with line breaks normalized to spaces. Markup is preserved as-is.</param>
public record SubtitleCue(double Start, double End, string Text);
