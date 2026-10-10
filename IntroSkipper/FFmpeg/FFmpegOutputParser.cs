// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IntroSkipper.Data;
using IntroSkipper.Helper;
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
    /// Picks the subtitle streams to read out of ffprobe's
    /// <c>-show_entries stream=index,codec_name:stream_tags=language -of csv=p=0</c> listing:
    /// those ffmpeg can decode to text, in a language of <paramref name="languages"/>.
    /// </summary>
    /// <param name="raw">ffprobe output, one <c>index,codec_name,language</c> line per subtitle stream; an untagged stream's line ends after the codec.</param>
    /// <param name="languages">The languages to read.</param>
    /// <returns>The stream indexes of the selected text subtitle streams, in listing order.</returns>
    internal static int[] ParseTextSubtitleStreams(string raw, SubtitleLanguageSelection languages)
    {
        List<int> streams = [];
        foreach (var line in raw.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Split(',', 3) is [var index, var codec, .. var language]
                && _textSubtitleCodecs.Contains(codec)
                && languages.Includes(language.FirstOrDefault())
                && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var streamIndex))
            {
                streams.Add(streamIndex);
            }
        }

        return [.. streams];
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse timestamp: {PtsTimeStr} from line: {Line}")]
    private static partial void LogFailedToParseTimestamp(ILogger logger, string ptsTimeStr, string line);
}
