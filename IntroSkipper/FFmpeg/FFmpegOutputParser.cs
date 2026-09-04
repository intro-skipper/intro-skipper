// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Parses FFmpeg output into Intro Skipper data types.
/// </summary>
internal static partial class FFmpegOutputParser
{
    private static readonly Regex _silenceDetectionExpression = SilenceRegex();

    private static readonly Regex _blackFrameRegex = BlackFrameRegex();

    private static readonly Regex _blackIntervalLogRegex = BlackIntervalLogRegex();

    private static readonly Regex _keyframeVisualTimeRegex = KeyframeVisualTimeRegex();

    private static readonly Regex _keyframeEntropyRegex = KeyframeEntropyRegex();

    private static readonly Regex _keyframeSaturationRegex = KeyframeSaturationRegex();

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

        /* Parse the per-keyframe metadata emitted by "entropy,signalstats,metadata=print".
         *
         * Sample output (one block per keyframe):
         * [Parsed_metadata_2 @ 0x0] frame:1 pts:20480 pts_time:2
         * [Parsed_metadata_2 @ 0x0] lavfi.entropy.normalized_entropy.normal.Y=0.000000
         * [Parsed_metadata_2 @ 0x0] lavfi.signalstats.SATAVG=33
         */
        double? time = null;
        var entropy = 0d;
        var saturation = 0d;
        var hasEntropy = false;
        var hasSaturation = false;

        foreach (var line in raw.Split('\n'))
        {
            var timeMatch = _keyframeVisualTimeRegex.Match(line);
            if (timeMatch.Success)
            {
                if (time is not null && hasEntropy && hasSaturation)
                {
                    visuals.Add(new KeyframeVisual(time.Value, entropy, saturation));
                }

                time = ParseDouble(timeMatch.Groups["time"].Value);
                entropy = 0d;
                saturation = 0d;
                hasEntropy = false;
                hasSaturation = false;
                continue;
            }

            var entropyMatch = _keyframeEntropyRegex.Match(line);
            if (entropyMatch.Success)
            {
                entropy = ParseDouble(entropyMatch.Groups["value"].Value);
                hasEntropy = true;
                continue;
            }

            var saturationMatch = _keyframeSaturationRegex.Match(line);
            if (saturationMatch.Success)
            {
                saturation = ParseDouble(saturationMatch.Groups["value"].Value);
                hasSaturation = true;
            }
        }

        if (time is not null && hasEntropy && hasSaturation)
        {
            visuals.Add(new KeyframeVisual(time.Value, entropy, saturation));
        }

        return [.. visuals];
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

    private static double ParseDouble(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    [GeneratedRegex("silence_(?<type>start|end): (?<time>[0-9\\.]+)")]
    private static partial Regex SilenceRegex();

    [GeneratedRegex(@"\[Parsed_blackframe_0 @ [^\]]+\] frame:(\d+) pblack:(\d+) .*? t:([\d.]+)")]
    private static partial Regex BlackFrameRegex();

    [GeneratedRegex(@"black_start:(?<start>[-+]?(?:\d+(?:\.\d*)?|\.\d+))\s+black_end:(?<end>[-+]?(?:\d+(?:\.\d*)?|\.\d+))\s+black_duration:(?<duration>[-+]?(?:\d+(?:\.\d*)?|\.\d+))")]
    private static partial Regex BlackIntervalLogRegex();

    [GeneratedRegex(@"pts_time:(?<time>-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)")]
    private static partial Regex KeyframeVisualTimeRegex();

    [GeneratedRegex(@"lavfi\.entropy\.normalized_entropy\.normal\.Y=(?<value>-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)")]
    private static partial Regex KeyframeEntropyRegex();

    [GeneratedRegex(@"lavfi\.signalstats\.SATAVG=(?<value>-?[0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)")]
    private static partial Regex KeyframeSaturationRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse timestamp: {PtsTimeStr} from line: {Line}")]
    private static partial void LogFailedToParseTimestamp(ILogger logger, string ptsTimeStr, string line);
}
