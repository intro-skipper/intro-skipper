// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IntroSkipper.Subtitles;

/// <summary>
/// Parses text subtitle payloads (SubRip, WebVTT, and ASS/SSA) into a flat list of timed
/// <see cref="SubtitleCue"/> values. The parser is intentionally lenient: it scans for cue
/// timing lines and tolerates missing indices, BOMs, CRLF line endings, and cue settings.
/// </summary>
/// <remarks>
/// Intro Skipper extracts subtitles with <c>ffmpeg -f srt</c>, so <see cref="ParseSubRip"/> is the
/// hot path; the WebVTT and ASS parsers exist for reading external sidecar files without a second
/// ffmpeg invocation.
/// </remarks>
public static partial class SubtitleParser
{
    /// <summary>
    /// Detects the subtitle format from a payload's header/structure and parses it.
    /// </summary>
    /// <param name="payload">Raw subtitle text.</param>
    /// <returns>Parsed cues in document order. Empty when nothing could be parsed.</returns>
    public static IReadOnlyList<SubtitleCue> Parse(string? payload)
    {
        return Parse(payload, DetectFormat(payload));
    }

    /// <summary>
    /// Parses a subtitle payload using the supplied format.
    /// </summary>
    /// <param name="payload">Raw subtitle text.</param>
    /// <param name="format">The format to parse as.</param>
    /// <returns>Parsed cues in document order. Empty when nothing could be parsed.</returns>
    public static IReadOnlyList<SubtitleCue> Parse(string? payload, SubtitleFormat format)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        return format switch
        {
            SubtitleFormat.Ass => ParseAss(payload),
            // SubRip and WebVTT share a cue grammar ("start --> end" + text lines); the timestamp
            // tokenizer accepts both ',' and '.' millisecond separators, so one routine handles both.
            _ => ParseTimedBlocks(payload),
        };
    }

    /// <summary>
    /// Parses a SubRip (.srt) payload.
    /// </summary>
    /// <param name="payload">Raw SubRip text.</param>
    /// <returns>Parsed cues in document order.</returns>
    public static IReadOnlyList<SubtitleCue> ParseSubRip(string payload) => ParseTimedBlocks(payload);

    /// <summary>
    /// Parses a WebVTT (.vtt) payload.
    /// </summary>
    /// <param name="payload">Raw WebVTT text.</param>
    /// <returns>Parsed cues in document order.</returns>
    public static IReadOnlyList<SubtitleCue> ParseWebVtt(string payload) => ParseTimedBlocks(payload);

    /// <summary>
    /// Detects the subtitle format from a payload's leading bytes/structure.
    /// </summary>
    /// <param name="payload">Raw subtitle text.</param>
    /// <returns>The detected format, or <see cref="SubtitleFormat.Unknown"/>.</returns>
    public static SubtitleFormat DetectFormat(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return SubtitleFormat.Unknown;
        }

        var trimmed = payload.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');

        if (trimmed.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            return SubtitleFormat.WebVtt;
        }

        if (trimmed.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("[Events]", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFormat.Ass;
        }

        if (TimeLineRegex().IsMatch(trimmed))
        {
            return SubtitleFormat.SubRip;
        }

        return SubtitleFormat.Unknown;
    }

    private static IReadOnlyList<SubtitleCue> ParseTimedBlocks(string payload)
    {
        var cues = new List<SubtitleCue>();
        var lines = SplitLines(payload);

        for (var i = 0; i < lines.Length; i++)
        {
            var match = TimeLineRegex().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            if (!TryParseTimestamp(match.Groups["start"].Value, out var start) ||
                !TryParseTimestamp(match.Groups["end"].Value, out var end))
            {
                continue;
            }

            // Collect text lines until the next blank line.
            var builder = new StringBuilder();
            var j = i + 1;
            for (; j < lines.Length && lines[j].Trim().Length > 0; j++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(lines[j].Trim());
            }

            i = j;

            var text = builder.ToString().Trim();
            if (text.Length > 0 && end > start)
            {
                cues.Add(new SubtitleCue(start, end, text));
            }
        }

        return cues;
    }

    private static IReadOnlyList<SubtitleCue> ParseAss(string payload)
    {
        var cues = new List<SubtitleCue>();

        foreach (var rawLine in SplitLines(payload))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // ASS Dialogue: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text
            // Split on the first 9 commas only; the 10th field (Text) may itself contain commas.
            var body = line["Dialogue:".Length..].TrimStart();
            var fields = body.Split(',', 10);
            if (fields.Length < 10)
            {
                continue;
            }

            if (!TryParseTimestamp(fields[1].Trim(), out var start) ||
                !TryParseTimestamp(fields[2].Trim(), out var end))
            {
                continue;
            }

            // ASS uses "\N" for hard line breaks; normalize to spaces.
            var text = fields[9].Replace("\\N", " ", StringComparison.Ordinal).Replace("\\n", " ", StringComparison.Ordinal).Trim();
            if (text.Length > 0 && end > start)
            {
                cues.Add(new SubtitleCue(start, end, text));
            }
        }

        return cues;
    }

    private static string[] SplitLines(string payload)
        => payload.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>
    /// Parses a subtitle timestamp of the form <c>HH:MM:SS,mmm</c>, <c>HH:MM:SS.mmm</c>, or
    /// <c>MM:SS.mmm</c> into seconds.
    /// </summary>
    /// <param name="value">The timestamp token.</param>
    /// <param name="seconds">The parsed time in seconds when successful.</param>
    /// <returns><see langword="true"/> when the token was a valid timestamp.</returns>
    internal static bool TryParseTimestamp(string value, out double seconds)
    {
        seconds = 0;
        var match = TimestampRegex().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var hours = match.Groups["h"].Success
            ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture)
            : 0;
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var secs = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        var millis = match.Groups["ms"].Success
            ? int.Parse(match.Groups["ms"].Value.PadRight(3, '0')[..3], CultureInfo.InvariantCulture)
            : 0;

        seconds = (hours * 3600) + (minutes * 60) + secs + (millis / 1000.0);
        return true;
    }

    // Matches a cue timing line, capturing the two endpoints. Cue settings after the end timestamp
    // (WebVTT, e.g. "align:start position:50%") are ignored.
    [GeneratedRegex(@"(?<start>\d{1,2}:\d{2}(?::\d{2})?[.,]\d{1,3})\s*-->\s*(?<end>\d{1,2}:\d{2}(?::\d{2})?[.,]\d{1,3})")]
    private static partial Regex TimeLineRegex();

    // Matches a single timestamp; hours are optional (WebVTT allows MM:SS.mmm).
    [GeneratedRegex(@"^(?:(?<h>\d{1,2}):)?(?<m>\d{2}):(?<s>\d{2})[.,](?<ms>\d{1,3})$")]
    private static partial Regex TimestampRegex();
}
