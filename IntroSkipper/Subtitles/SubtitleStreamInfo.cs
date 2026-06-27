// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Subtitles;

/// <summary>
/// Describes a single subtitle stream discovered in a media container via <c>ffprobe</c>.
/// </summary>
/// <param name="Index">The absolute stream index inside the container (ffprobe <c>stream.index</c>).</param>
/// <param name="Codec">The ffprobe <c>codec_name</c> (e.g. <c>subrip</c>, <c>hdmv_pgs_subtitle</c>).</param>
/// <param name="Language">The BCP-47/ISO-639 language tag from <c>stream_tags.language</c>, or <see langword="null"/> when untagged.</param>
/// <param name="IsTextBased">Whether <see cref="Codec"/> is a text codec extractable without OCR.</param>
/// <param name="IsForced">Whether the stream carries the <c>forced</c> disposition (often used for on-screen text such as recap cards).</param>
public record SubtitleStreamInfo(
    int Index,
    string Codec,
    string? Language,
    bool IsTextBased,
    bool IsForced);
