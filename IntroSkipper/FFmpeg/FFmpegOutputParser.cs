// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Parses FFmpeg output into Intro Skipper data types.
/// </summary>
internal static partial class FFmpegOutputParser
{
    // The stats KeyframeSignalStatRegex admits, so a block with this many distinct matches has them all.
    private const int KeyframeSignalStatCount = 6;

    private static readonly Regex _silenceDetectionExpression = SilenceRegex();

    private static readonly Regex _blackFrameRegex = BlackFrameRegex();

    private static readonly Regex _blackIntervalLogRegex = BlackIntervalLogRegex();

    private static readonly Regex _keyframeVisualTimeRegex = KeyframeVisualTimeRegex();

    private static readonly Regex _keyframeSignalStatRegex = KeyframeSignalStatRegex();

    private static readonly Regex _showInfoRegex = ShowInfoRegex();

    private static readonly Regex _mappedStreamRegex = MappedStreamRegex();

    // Subtitle codecs that jellyfin-ffmpeg 7.1.3 and 8.1.2 both decode to text (their -decoders
    // listings agree), which -c:s webvtt can re-encode. Left out: the image codecs
    // (dvb_subtitle, dvd_subtitle, hdmv_pgs_subtitle, xsub), which would need OCR, and the
    // codecs neither build can decode (arib_caption, dvb_teletext, hdmv_text_subtitle,
    // ivtv_vbi, ttml).
    private static readonly FrozenSet<string> _textSubtitleCodecs = new[]
    {
        "ass", "eia_608", "jacosub", "microdvd", "mov_text", "mpl2", "pjs", "realtext", "sami",
        "srt", "ssa", "stl", "subrip", "subviewer", "subviewer1", "text", "vplayer", "webvtt",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static TimeRange[] ParseSilence(string raw, double rangeStart)
    {
        var currentRange = new TimeRange();
        var silenceRanges = new List<TimeRange>();

        foreach (Match match in _silenceDetectionExpression.Matches(raw))
        {
            var isStart = match.Groups["type"].Value == "start";
            var time = Convert.ToDouble(match.Groups["time"].Value, CultureInfo.InvariantCulture);

            if (isStart)
            {
                currentRange.Start = time + rangeStart;
            }
            else
            {
                currentRange.End = time + rangeStart;
                silenceRanges.Add(new TimeRange(currentRange));
            }
        }

        return [.. silenceRanges];
    }

    internal static double[] ParseKeyFrames(string raw, double rangeStart, ILogger? logger = null)
    {
        var keyframes = new List<double>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var ptsIndex = line.IndexOf("pts_time:", StringComparison.OrdinalIgnoreCase);
            if (ptsIndex == -1)
            {
                continue;
            }

            var ptsTimeStr = line[(ptsIndex + 9)..].Split(' ', 2)[0];

            if (double.TryParse(ptsTimeStr, CultureInfo.InvariantCulture, out var timestamp))
            {
                keyframes.Add(timestamp + rangeStart);
            }
            else if (logger is not null)
            {
                LogFailedToParseTimestamp(logger, ptsTimeStr, line);
            }
        }

        return [.. keyframes];
    }

    internal static BlackFrame[] ParseBlackFrames(string raw)
    {
        var blackFrames = new List<BlackFrame>();
        /* Run the blackframe filter.
         *
         * Sample output:
         * [Parsed_blackframe_0 @ 0x0000000] frame:1 pblack:99 pts:43 t:0.043000 type:B last_keyframe:0
         * [Parsed_blackframe_0 @ 0x0000000] frame:2 pblack:99 pts:85 t:0.085000 type:B last_keyframe:0
         */
        foreach (var line in raw.Split('\n'))
        {
            var match = _blackFrameRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var frame = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var percentage = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var time = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

            blackFrames.Add(new BlackFrame(percentage, time, frame));
        }

        return [.. blackFrames];
    }

    internal static KeyframeVisual[] ParseKeyframeVisuals(string raw)
    {
        var visuals = new List<KeyframeVisual>();

        /* Parse the per-keyframe metadata emitted by "signalstats,metadata=print".
         *
         * Sample output (one block per keyframe, other signalstats lines omitted):
         * [Parsed_metadata_2 @ 0x0] frame:1 pts:20480 pts_time:2
         * [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMIN=16
         * [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YLOW=16
         * [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YHIGH=16
         * [Parsed_metadata_2 @ 0x0] lavfi.signalstats.YMAX=235
         * [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATLOW=0
         * [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=33
         */
        double? time = null;
        var stats = new Dictionary<string, double>(KeyframeSignalStatCount, StringComparer.Ordinal);

        foreach (var line in raw.Split('\n'))
        {
            var timeMatch = _keyframeVisualTimeRegex.Match(line);
            if (timeMatch.Success)
            {
                AddKeyframeVisual(visuals, time, stats);
                time = ParseDouble(timeMatch.Groups["time"].Value);
                stats.Clear();
                continue;
            }

            var statMatch = _keyframeSignalStatRegex.Match(line);
            if (statMatch.Success)
            {
                stats[statMatch.Groups["name"].Value] = ParseDouble(statMatch.Groups["value"].Value);
            }
        }

        AddKeyframeVisual(visuals, time, stats);

        return [.. visuals];
    }

    // A block missing any stat, such as one truncated at the end of the output, is dropped rather
    // than emitted with zeros that would pass the card gate.
    private static void AddKeyframeVisual(List<KeyframeVisual> visuals, double? time, Dictionary<string, double> stats)
    {
        if (time is { } keyframeTime && stats.Count == KeyframeSignalStatCount)
        {
            visuals.Add(new KeyframeVisual(keyframeTime, stats["YMIN"], stats["YLOW"], stats["YHIGH"], stats["YMAX"], stats["SATLOW"], stats["SATAVG"]));
        }
    }

    /// <summary>
    /// Parses the showinfo filter's log: the presentation time and size of every frame it saw, in order.
    /// </summary>
    /// <param name="raw">The ffmpeg standard error output.</param>
    /// <returns>One entry per frame.</returns>
    internal static ShowInfoFrame[] ParseShowInfo(string raw)
    {
        var frames = new List<ShowInfoFrame>();
        foreach (Match match in _showInfoRegex.Matches(raw))
        {
            frames.Add(new ShowInfoFrame(
                double.Parse(match.Groups["time"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture)));
        }

        return [.. frames];
    }

    internal static BlackInterval[] ParseBlackIntervals(string raw)
    {
        var blackIntervals = new List<BlackInterval>();

        foreach (var line in raw.Split('\n'))
        {
            var logMatch = _blackIntervalLogRegex.Match(line);
            if (!logMatch.Success)
            {
                continue;
            }

            var start = ParseDouble(logMatch.Groups["start"].Value);
            var end = ParseDouble(logMatch.Groups["end"].Value);
            var duration = ParseDouble(logMatch.Groups["duration"].Value);

            if (end > start && duration > 0)
            {
                blackIntervals.Add(new BlackInterval(start, end));
            }
        }

        return [.. blackIntervals];
    }

    /// <summary>
    /// Reads ffprobe's <c>-show_entries stream=index,codec_name:stream_tags=language:format=format_name,start_time
    /// -of compact=p=1:nk=1</c> listing: the subtitle streams ffmpeg can decode to text, in a
    /// language of <paramref name="languages"/>, and the file's start time when it is Matroska.
    /// </summary>
    /// <param name="raw">ffprobe output: one <c>stream|index|codec_name|language</c> line per subtitle stream, where an untagged stream's line ends after the codec, and one <c>format|format_name|start_time</c> line.</param>
    /// <param name="languages">The languages to read.</param>
    /// <returns>The selected text subtitle streams in listing order, and the Matroska start time.</returns>
    internal static SubtitleStreamListing ParseSubtitleStreamListing(string raw, SubtitleLanguageSelection languages)
    {
        List<int> streams = [];
        double? matroskaStartTime = null;
        foreach (var line in raw.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|');
            if (fields is ["stream", var index, var codec, .. var language]
                && _textSubtitleCodecs.Contains(codec)
                && languages.Includes(language.FirstOrDefault())
                && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var streamIndex))
            {
                streams.Add(streamIndex);
            }
            else if (fields is ["format", var formats, var start]
                && formats.Split(',').Contains("matroska", StringComparer.Ordinal)
                && double.TryParse(start, NumberStyles.Float, CultureInfo.InvariantCulture, out var startTime))
            {
                matroskaStartTime = startTime;
            }
        }

        return new([.. streams], matroskaStartTime);
    }

    /// <summary>
    /// Reads the codec of the input stream ffmpeg mapped to its first output stream, from the
    /// stream mapping block a run prints at the info log level.
    /// </summary>
    /// <remarks>
    /// The block is a line that reads exactly <c>Stream mapping:</c>, followed by one indented
    /// line per output stream, such as <c>  Stream #0:1 -&gt; #0:0 (hevc (native) -&gt; wrapped_avframe (native))</c>.
    /// A tag value can hold the same text, but ffmpeg prints every line of a tag value after
    /// the tag's name column, so no tag line reads exactly <c>Stream mapping:</c>. Checked on
    /// jellyfin-ffmpeg 7.1.3 and 8.1.2.
    /// </remarks>
    /// <param name="raw">The ffmpeg standard error output.</param>
    /// <returns>The mapped input stream's codec name, such as <c>hevc</c>, or <see langword="null"/> when the output has no mapping block, as for a file without a video stream.</returns>
    internal static string? ParseMappedVideoCodec(string raw)
    {
        var inBlock = false;
        foreach (var line in raw.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (!inBlock)
            {
                inBlock = text == "Stream mapping:";
                continue;
            }

            if (!text.StartsWith("  Stream #", StringComparison.Ordinal))
            {
                return null;
            }

            if (_mappedStreamRegex.Match(text) is { Success: true } match)
            {
                return match.Groups["codec"].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses ffprobe's JSON chapter listing into the chapters Jellyfin's own probe would store
    /// in its chapter table for the file.
    /// </summary>
    /// <remarks>
    /// ffprobe prints each <c>start_time</c> as a string of seconds, such as <c>"90.048625"</c>,
    /// on jellyfin-ffmpeg 7.1.3 and 8.1.2. A chapter whose start is missing, not such a string,
    /// or not a representable tick count (<see cref="TickConversions.TryFromSeconds"/>) is
    /// dropped. A chapterless file still prints an empty <c>chapters</c> array, so output
    /// without one is unusable, not a file without chapters.
    /// The chapters are shaped as Jellyfin stores them, so for a file no other plugin touched
    /// both chapter sources hold the same list: the start is rounded to the millisecond as
    /// <c>ProbeResultNormalizer.GetChapterInfo</c> rounds it, a chapter whose title is blank or
    /// a timestamp is named with <paramref name="chapterNameFormat"/> and its number in
    /// ffprobe's order as <c>FFProbeVideoInfo.NormalizeChapterNames</c> names it, and chapters
    /// at or past the runtime are dropped as <c>ChapterManager.SaveChapters</c> drops them.
    /// Jellyfin keeps a chapter with a negative start, which this drops.
    /// </remarks>
    /// <param name="raw">JSON returned by <c>ffprobe -show_chapters -of json</c>.</param>
    /// <param name="runtimeTicks">The item's runtime in ticks.</param>
    /// <param name="chapterNameFormat">Jellyfin's chapter name format under the server's language, with <c>{0}</c> for the chapter number.</param>
    /// <returns>The chapters sorted by start.</returns>
    /// <exception cref="JsonException"><paramref name="raw"/> is not valid JSON, or has no top-level <c>chapters</c> array.</exception>
    internal static ChapterInfo[] ParseChapters(string raw, long runtimeTicks, string chapterNameFormat)
    {
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("chapters", out var chapters)
            || chapters.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("ffprobe printed no chapters array.");
        }

        var result = new List<ChapterInfo>();
        var number = 0;
        foreach (var chapter in chapters.EnumerateArray())
        {
            number++;
            if (chapter.ValueKind != JsonValueKind.Object
                || !chapter.TryGetProperty("start_time", out var start)
                || start.ValueKind != JsonValueKind.String
                || !double.TryParse(start.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var startSeconds)
                || !TickConversions.TryFromSeconds(startSeconds, out _))
            {
                continue;
            }

            // Jellyfin's expression, so the two agree at a half millisecond too (to even).
            var startTicks = TimeSpan.FromMilliseconds(Math.Round(TimeSpan.FromSeconds(startSeconds).TotalMilliseconds)).Ticks;
            if (startTicks >= runtimeTicks)
            {
                continue;
            }

            var title = chapter.TryGetProperty("tags", out var tags)
                && tags.ValueKind == JsonValueKind.Object
                && tags.TryGetProperty("title", out var titleTag)
                && titleTag.ValueKind == JsonValueKind.String
                ? titleTag.GetString()
                : null;
            // Jellyfin's TimeSpan.TryParse(name, out _) reads the current culture; this runs in
            // the same process, so the same titles count as timestamps.
            var name = string.IsNullOrWhiteSpace(title) || TimeSpan.TryParse(title, CultureInfo.CurrentCulture, out _)
                ? string.Format(CultureInfo.InvariantCulture, chapterNameFormat, number)
                : title;
            result.Add(new ChapterInfo { StartPositionTicks = startTicks, Name = name });
        }

        return [.. result.OrderBy(chapter => chapter.StartPositionTicks)];
    }

    /// <summary>
    /// Parses FFmpeg's WebVTT subtitle output. Cue settings after the end time are dropped;
    /// subtitle markup stays in the text because the analyzer owns normalization for
    /// user-configured expressions.
    /// </summary>
    /// <param name="raw">WebVTT output.</param>
    /// <returns>Well-formed timed cues.</returns>
    internal static SubtitleCue[] ParseWebVtt(string raw)
    {
        List<SubtitleCue> cues = [];
        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length;)
        {
            var timing = lines[i].IndexOf("-->", StringComparison.Ordinal);
            if (timing < 0)
            {
                i++;
                continue;
            }

            var startText = lines[i][..timing].Trim();
            var endText = lines[i][(timing + 3)..].Trim().Split(' ', 2)[0];
            if (!TryParseVttTime(startText, out var start) || !TryParseVttTime(endText, out var end) || end <= start)
            {
                i++;
                continue;
            }

            var text = new StringBuilder();
            i++;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(lines[i].Trim());
                i++;
            }

            if (text.Length > 0)
            {
                cues.Add(new SubtitleCue(start, end, text.ToString()));
            }
        }

        return [.. cues];
    }

    private static bool TryParseVttTime(string value, out double seconds)
    {
        seconds = 0;
        var parts = value.Split(':');
        if (parts.Length is not (2 or 3)
            || !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var second)
            || !double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var minute))
        {
            return false;
        }

        var hour = 0d;
        if (parts.Length == 3 && !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out hour))
        {
            return false;
        }

        seconds = (hour * 3600) + (minute * 60) + second;
        return seconds >= 0;
    }

    private static double ParseDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    [GeneratedRegex("silence_(?<type>start|end): (?<time>[0-9\\.]+)")]
    private static partial Regex SilenceRegex();

    // The index after Parsed_blackframe_ is the filter's position in its chain, so it is not pinned.
    [GeneratedRegex(@"\[Parsed_blackframe_\d+ @ [^\]]+\] frame:(\d+) pblack:(\d+) .*? t:([\d.]+)")]
    private static partial Regex BlackFrameRegex();

    [GeneratedRegex(@"black_start:(?<start>[-+]?(?:\d+(?:\.\d*)?|\.\d+))\s+black_end:(?<end>[-+]?(?:\d+(?:\.\d*)?|\.\d+))\s+black_duration:(?<duration>[-+]?(?:\d+(?:\.\d*)?|\.\d+))")]
    private static partial Regex BlackIntervalLogRegex();

    [GeneratedRegex(@"pts_time:(?<time>-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)")]
    private static partial Regex KeyframeVisualTimeRegex();

    [GeneratedRegex(@"lavfi\.signalstats\.(?<name>YMIN|YLOW|YHIGH|YMAX|SATLOW|SATAVG)=(?<value>-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)")]
    private static partial Regex KeyframeSignalStatRegex();

    /*
     * Sample output:
     * [Parsed_showinfo_3 @ 0x0] n:   0 pts:     17 pts_time:0.017   duration:     41 duration_time:0.041   fmt:gray cl:left sar:1/1 s:320x240 i:P iskey:0 type:B checksum:6B847A12 plane_checksum:[6B847A12] mean:[32] stdev:[42.1]
     */
    [GeneratedRegex(@"\[Parsed_showinfo_\d+ @ [^\]]+\] n:\s*\d+ pts:\s*-?\d+ pts_time:(?<time>-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)[^\n]*? s:(?<width>\d+)x(?<height>\d+)")]
    private static partial Regex ShowInfoRegex();

    // A stream mapping line for the first output stream, such as "  Stream #0:1 -> #0:0 (hevc (native) -> ...".
    [GeneratedRegex(@"^  Stream #\d+:\d+ -> #0:0 \((?<codec>[^\s()]+) ")]
    private static partial Regex MappedStreamRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse timestamp: {PtsTimeStr} from line: {Line}")]
    private static partial void LogFailedToParseTimestamp(ILogger logger, string ptsTimeStr, string line);

    /// <summary>
    /// The subtitle streams to read from a media file, and what a head read needs to know about it.
    /// </summary>
    /// <param name="Streams">The selected text subtitle streams, by stream index.</param>
    /// <param name="MatroskaStartTime">The file's start time in seconds when it is Matroska, else <see langword="null"/>.</param>
    internal sealed record SubtitleStreamListing(int[] Streams, double? MatroskaStartTime);
}
