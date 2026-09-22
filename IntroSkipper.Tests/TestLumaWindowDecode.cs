// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Analyzers.Credits;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class TestLumaWindowDecode
{
    [Fact]
    public void ParseShowInfo_ReadsTimeAndSizeOfEveryFrame()
    {
        const string raw = """
            [Parsed_showinfo_3 @ 0000020f183d74c0] config in time_base: 1/1000, frame_rate: 24/1
            [Parsed_showinfo_3 @ 0000020f183d74c0] config out time_base: 0/0, frame_rate: 0/0
            [Parsed_showinfo_3 @ 0000020f183d74c0] n:   0 pts:     17 pts_time:0.017   duration:     41 duration_time:0.041   fmt:gray cl:left sar:1/1 s:320x240 i:P iskey:0 type:B checksum:6B847A12 plane_checksum:[6B847A12] mean:[32] stdev:[42.1]
            [Parsed_showinfo_3 @ 0000020f183d74c0] n:   1 pts:     58 pts_time:0.058   duration:     42 duration_time:0.042   fmt:gray cl:left sar:1/1 s:320x240 i:P iskey:0 type:B checksum:6B847A12 plane_checksum:[6B847A12] mean:[32] stdev:[42.1]
            """;

        var frames = FFmpegOutputParser.ParseShowInfo(raw);

        Assert.Equal([new ShowInfoFrame(0.017, 320, 240), new ShowInfoFrame(0.058, 320, 240)], frames);
    }

    [FactSkipFFmpegTests]
    public async Task DecodeLumaWindowAsync_ReturnsFramesOnTheSourceScale()
    {
        // RGB 0x060606 lands at luma 21 on the limited-range scale the clip is encoded on; the
        // decode must read it as 21, not stretched toward 0 as a grey conversion would.
        var path = await GreyClipAsync("0x060606", seconds: 2);
        try
        {
            var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "grey", Path = path, Duration = 2 };

            var window = await FfmpegTestHelpers.CreateFFmpegService().DecodeLumaWindowAsync(episode, new TimeRange(0.5, 1.0), 32);

            Assert.NotNull(window);
            Assert.Equal(32, window.Width);
            Assert.Equal(18, window.Height);
            Assert.InRange(window.FrameCount, 11, 13);
            Assert.All(window.Times, time => Assert.InRange(time, 0.5, 1.0));
            Assert.Equal(window.Times.OrderBy(time => time), window.Times);
            var measure = LeadInProbe.Measure(window.Frame(0), window.Width, new bool[window.Width * window.Height]);
            Assert.InRange(measure.Background, 20, 22);
            Assert.Equal(0, measure.ForegroundFraction);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [FactSkipFFmpegTests]
    public async Task DecodeLumaWindowAsync_NonzeroExit_ReturnsNull()
    {
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "missing", Path = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mkv"), Duration = 2 };

        var window = await FfmpegTestHelpers.CreateFFmpegService().DecodeLumaWindowAsync(episode, new TimeRange(0, 1), 32);

        Assert.Null(window);
    }

    [FactSkipFFmpegTests]
    public async Task RunCapturedAsync_StopsAtTheByteCap()
    {
        // A minute of frames is far more than the cap; the runner must kill ffmpeg at the cap and
        // report the cut rather than keep reading.
        var capture = await new FFmpegProcessRunner(NullLogger.Instance).RunCapturedAsync(
            "ffmpeg",
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=64x36:r=24:d=60", "-f", "rawvideo", "-"],
            maximumStdoutBytes: 10_000,
            timeout: 30_000);

        Assert.True(capture.StdoutTruncated);
        Assert.True(capture.Stdout.Length <= 10_000);
    }

    private static async Task<string> GreyClipAsync(string colour, int seconds)
    {
        var path = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-luma.mkv");
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            ["-y", "-v", "error", "-f", "lavfi", "-i", $"color=c={colour}:s=64x36:r=24:d={seconds}", "-pix_fmt", "yuv420p", "-c:v", "ffv1", path]);
        return path;
    }
}
