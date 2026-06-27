// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;

namespace IntroSkipper.Subtitles;

/// <summary>
/// Classifies subtitle codecs as text-based (cheaply extractable to plain text by ffmpeg)
/// or image-based (require OCR and are therefore out of scope for phrase detection).
/// </summary>
/// <remarks>
/// The codec strings are the <c>codec_name</c> values reported by <c>ffprobe -show_streams</c>.
/// They were verified against the local ffmpeg decoder list (see docs/recap-research/A-subtitles.md).
/// </remarks>
public static class SubtitleCodec
{
    // ffprobe codec_name values for text subtitle streams that ffmpeg can transcode to SubRip/WebVTT.
    private static readonly FrozenSet<string> _textCodecs = new[]
    {
        "subrip",
        "srt",
        "text",
        "ssa",
        "ass",
        "webvtt",
        "mov_text",
        "subviewer",
        "subviewer1",
        "sami",
        "microdvd",
        "mpl2",
        "jacosub",
        "pjs",
        "realtext",
        "vplayer",
        "stl",
        "eia_608",
        "cc_dec",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // ffprobe codec_name values for bitmap subtitle streams. Listed for explicit, testable rejection.
    private static readonly FrozenSet<string> _imageCodecs = new[]
    {
        "hdmv_pgs_subtitle",
        "dvd_subtitle",
        "dvb_subtitle",
        "dvbsub",
        "xsub",
        "dvb_teletext",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether the given ffprobe <c>codec_name</c> identifies a text subtitle stream
    /// that can be extracted to plain text without OCR.
    /// </summary>
    /// <param name="codecName">The ffprobe <c>codec_name</c> value (e.g. <c>subrip</c>, <c>hdmv_pgs_subtitle</c>).</param>
    /// <returns><see langword="true"/> for a known text codec; otherwise <see langword="false"/> (including unknown codecs, which are treated as non-text to stay conservative).</returns>
    public static bool IsTextBased(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return false;
        }

        return _textCodecs.Contains(codecName);
    }

    /// <summary>
    /// Determines whether the given ffprobe <c>codec_name</c> identifies a known bitmap subtitle
    /// stream that would require OCR.
    /// </summary>
    /// <param name="codecName">The ffprobe <c>codec_name</c> value.</param>
    /// <returns><see langword="true"/> for a known image codec; otherwise <see langword="false"/>.</returns>
    public static bool IsImageBased(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return false;
        }

        return _imageCodecs.Contains(codecName);
    }
}
