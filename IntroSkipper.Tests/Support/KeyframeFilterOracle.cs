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
    /// Decodes the keyframes through both filters, as one ffmpeg with two null outputs.
    /// </summary>
    /// <param name="path">The media file.</param>
    /// <param name="start">Seconds to seek to.</param>
    /// <param name="threshold">blackframe's threshold.</param>
    /// <param name="selectKeyframes">Whether the source is VP9, whose decoder ignores <c>-skip_frame nokey</c>.</param>
    /// <returns>One page per keyframe. A page's visual carries the black-frame time and its frame counter is 0, as on the plugin's pages.</returns>
    internal static async Task<KeyframePage[]> PagesAsync(string path, double start, int threshold, bool selectKeyframes = false)
    {
        var select = selectKeyframes ? "select=eq(key\\,1)," : string.Empty;
        var capture = await new FFmpegProcessRunner(NullLogger.Instance).RunCapturedAsync(
            "ffmpeg",
            [
                "-hide_banner", "-loglevel", "info",
                "-skip_frame", "nokey",
                "-ss", start.ToString(CultureInfo.InvariantCulture),
                "-i", path,
                "-an", "-dn", "-sn", "-vf", select + "blackframe=amount=0:threshold=" + threshold.ToString(CultureInfo.InvariantCulture), "-f", "null", "-",
                "-an", "-dn", "-sn", "-vf", select + "format=yuv420p,signalstats,metadata=print", "-f", "null", "-",
            ],
            maximumBytesPerStream: 64L * 1024 * 1024,
            expectedStdoutBytes: 0,
            timeout: 120_000);

        var rows = FFmpegOutputParser.ParseBlackFrames(capture.Stderr);
        var visuals = FFmpegOutputParser.ParseKeyframeVisuals(capture.Stderr);
        return [.. rows.Zip(visuals, (row, visual) => new KeyframePage(row with { Frame = 0 }, visual with { Time = row.Time }))];
    }
}
