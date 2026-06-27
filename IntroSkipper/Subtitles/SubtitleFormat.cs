// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Subtitles;

/// <summary>
/// Text subtitle container format understood by <see cref="SubtitleParser"/>.
/// </summary>
public enum SubtitleFormat
{
    /// <summary>
    /// Format could not be determined.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// SubRip (.srt). Also the format Intro Skipper requests from ffmpeg (<c>-f srt</c>).
    /// </summary>
    SubRip = 1,

    /// <summary>
    /// WebVTT (.vtt).
    /// </summary>
    WebVtt = 2,

    /// <summary>
    /// Advanced SubStation Alpha (.ass/.ssa).
    /// </summary>
    Ass = 3,
}
