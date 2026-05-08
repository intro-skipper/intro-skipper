// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Parses raw FFmpeg output into structured detection results.
/// </summary>
internal static partial class FFmpegOutputParser
{
    private static readonly Regex _silenceDetectionExpression = SilenceRegex();

    private static readonly Regex _blackFrameRegex = BlackFrameRegex();

    /// <summary>
    /// Parses raw FFmpeg silencedetect output into time ranges.
    /// </summary>
    /// <param name="raw">Raw FFmpeg stderr/stdout output.</param>
    /// <param name="rangeStart">Offset to add to all parsed timestamps.</param>
    /// <returns>Array of silence time ranges.</returns>
    internal static TimeRange[] ParseSilenceRaw(string raw, double rangeStart)
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

    /// <summary>
    /// Parses raw FFmpeg showinfo filter output into keyframe timestamps.
    /// </summary>
    /// <param name="raw">Raw FFmpeg stderr output.</param>
    /// <param name="rangeStart">Offset to add to all parsed timestamps.</param>
    /// <param name="logger">Optional logger for warning on unparseable timestamps.</param>
    /// <returns>Array of keyframe timestamps in seconds.</returns>
    internal static double[] ParseKeyFramesRaw(string raw, double rangeStart, ILogger? logger = null)
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

            if (double.TryParse(ptsTimeStr, CultureInfo.InvariantCulture, out double timestamp))
            {
                keyframes.Add(timestamp + rangeStart);
            }
            else
            {
                if (logger is { } parseLogger)
                {
                    LogFailedToParseTimestamp(parseLogger, ptsTimeStr, line);
                }
            }
        }

        return [.. keyframes];
    }

    /// <summary>
    /// Parses raw FFmpeg blackframe filter output into black frame data.
    /// </summary>
    /// <param name="raw">Raw FFmpeg stderr output.</param>
    /// <returns>Array of detected black frames.</returns>
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse timestamp: {PtsTimeStr} from line: {Line}")]
    private static partial void LogFailedToParseTimestamp(ILogger logger, string ptsTimeStr, string line);

    [GeneratedRegex("silence_(?<type>start|end): (?<time>[0-9\\.]+)")]
    private static partial Regex SilenceRegex();

    [GeneratedRegex(@"\[Parsed_blackframe_0 @ [^\]]+\] frame:(\d+) pblack:(\d+) .*? t:([\d.]+)")]
    private static partial Regex BlackFrameRegex();
}
