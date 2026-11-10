// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The pages ffmpeg's own blackframe and signalstats filters report for a keyframe decode, which
/// the plugin's pages must equal. Tests pin the C# measurements to it, so a future ffmpeg that moves
/// either filter's values shows up as a failure.
/// </summary>
internal static class KeyframeFilterOracle
{
    /// <summary>
    /// Decodes the keyframes through each filter in its own ffmpeg: two outputs of one run can
    /// interleave their log lines, and the parsers then lose a row.
    /// </summary>
    /// <param name="path">The media file.</param>
    /// <param name="start">Seconds to seek to.</param>
    /// <param name="threshold">blackframe's threshold.</param>
    /// <param name="selectKeyframes">Whether the source is VP9, whose decoder ignores <c>-skip_frame nokey</c>.</param>
    /// <returns>One page per keyframe, at the black-frame time.</returns>
    internal static async Task<KeyframePage[]> PagesAsync(string path, double start, int threshold, bool selectKeyframes = false)
    {
        var select = selectKeyframes ? @"select=eq(key\,1)," : string.Empty;
        var rows = FFmpegOutputParser.ParseBlackFrames(await LogOfAsync(path, start, select + "blackframe@oracle=amount=0:threshold=" + threshold.ToString(CultureInfo.InvariantCulture)), new FilterInstance("blackframe@oracle"));
        var frames = FFmpegOutputParser.ParseKeyframeVisuals(await LogOfAsync(path, start, select + "format=yuv420p,signalstats,metadata@oracle=print"), new FilterInstance("metadata@oracle"));
        return [.. rows.Zip(frames, (row, frame) => new KeyframePage(row.Time, row.Percentage, frame.Visual))];
    }

    private static async Task<string> LogOfAsync(string path, double start, string filters)
    {
        var capture = await new FFmpegProcessRunner(NullLogger.Instance).RunCapturedAsync(
            "ffmpeg",
            ["-hide_banner", "-loglevel", "info", "-skip_frame", "nokey", "-ss", start.ToString(CultureInfo.InvariantCulture), "-i", path, "-an", "-dn", "-sn", "-vf", filters, "-f", "null", "-"],
            maximumBytesPerStream: 64L * 1024 * 1024,
            expectedStdoutBytes: 0,
            timeout: 120_000);
        return capture.Stderr;
    }
}
