// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Pins every page fact against ffmpeg's blackframe and signalstats filters on real decodes.
/// </summary>
public sealed class TestPageFacts
{
    [TheorySkipFFmpegTests]
    [InlineData("video/credits.mp4", 5, 28)]
    [InlineData("video/credits.mp4", 5, 100)]
    [InlineData("video/cut-to-black.mp4", 0, 28)]
    [InlineData("video/moving-story.mp4", 0, 28)]
    [InlineData("video/rainbow.mp4", 0, 32)]
    [InlineData("video/seek-sample.mp4", 2, 28)]
    public async Task Fixture_MatchesTheFilters(string fixture, double start, int threshold)
    {
        var episode = FfmpegTestHelpers.QueueFile(fixture);

        var pages = await ScanAsync(episode, start, threshold);

        Assert.NotEmpty(pages);
        Assert.Equal(await KeyframeFilterOracle.PagesAsync(episode.Path, start, threshold), pages);
    }

    /// <summary>
    /// Each source reaches the pipe through another path. 65x37 rounds its chroma up, and the 10-bit
    /// sources take swscale's dither, in its limited and its full range form. Only H.264 decodes as
    /// yuvj420p and through auto_scale: ffv1 reports the same source as yuv420p in full range.
    /// </summary>
    [TheorySkipFFmpegTests]
    [InlineData("65x37", "yuv420p", "tv", "ffv1", 28)]
    [InlineData("65x37", "yuv420p", "tv", "ffv1", 100)]
    [InlineData("128x72", "yuvj420p", "pc", "libx264", 28)]
    [InlineData("128x72", "yuv422p", "tv", "ffv1", 28)]
    [InlineData("128x72", "yuv444p", "pc", "ffv1", 28)]
    [InlineData("128x72", "yuv420p10le", "tv", "ffv1", 28)]
    [InlineData("128x72", "yuv420p10le", "pc", "ffv1", 28)]
    [InlineData("128x72", "yuv420p10le", "tv", "libx265", 28)]
    [InlineData("128x72", "yuv420p", "tv", "libx264", 28)]
    public async Task GeneratedSource_MatchesTheFilters(string size, string pixelFormat, string range, string encoder, int threshold)
    {
        var path = await EncodeAsync(".mkv", size, pixelFormat, range, encoder);
        try
        {
            var pages = await ScanAsync(new QueuedEpisode { EpisodeId = Guid.NewGuid(), Path = path }, 0, threshold);

            Assert.NotEmpty(pages);
            Assert.Equal(await KeyframeFilterOracle.PagesAsync(path, 0, threshold), pages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// signalstats rounds a tie to even. At 65x37 the 10th percentile's rank is 240.5 and the 90th's
    /// 2164.5, which round down, so with exactly 240 and then 2164 dark pixels the rank still lands on
    /// them. At 69x35 they are 241.5 and 2173.5, which round up, so 241 and 2173 dark pixels leave it
    /// on the bright pixels.
    /// </summary>
    /// <param name="width">The frame's width.</param>
    /// <param name="height">The frame's height.</param>
    /// <param name="lowRankDark">Dark pixels of the frame that decides the 10th percentile.</param>
    /// <param name="highRankDark">Dark pixels of the frame that decides the 90th percentile.</param>
    /// <param name="expected">The luma both percentiles land on.</param>
    [TheorySkipFFmpegTests]
    [InlineData(65, 37, 240, 2164, 10)]
    [InlineData(69, 35, 241, 2173, 200)]
    public async Task PercentileRankOnATie_MatchesTheFilter(int width, int height, int lowRankDark, int highRankDark, int expected)
    {
        var pixels = width * height;
        var chroma = ((width + 1) / 2) * ((height + 1) / 2);
        var raw = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + ".yuv");
        var path = Path.ChangeExtension(raw, ".mkv");
        await File.WriteAllBytesAsync(raw, [.. Frame(lowRankDark), .. Frame(highRankDark)]);
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            ["-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", FormattableString.Invariant($"{width}x{height}"), "-r", "1", "-i", raw, "-c:v", "ffv1", path]);
        try
        {
            var pages = await ScanAsync(new QueuedEpisode { EpisodeId = Guid.NewGuid(), Path = path }, 0, 28);

            Assert.Equal(await KeyframeFilterOracle.PagesAsync(path, 0, 28), pages);
            Assert.Equal(expected, pages[0].Visual!.LumaLow);
            Assert.Equal(expected, pages[1].Visual!.LumaHigh);
        }
        finally
        {
            File.Delete(raw);
            File.Delete(path);
        }

        byte[] Frame(int darkPixels) =>
        [
            .. Enumerable.Repeat((byte)10, darkPixels),
            .. Enumerable.Repeat((byte)200, pixels - darkPixels),
            .. Enumerable.Repeat((byte)128, 2 * chroma),
        ];
    }

    /// <summary>
    /// VP9 keeps its keyframe select under the pipe. Without it every frame of this clip, three
    /// keyframes in 75 frames, would come through as a page.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task Vp9_KeepsItsKeyframeSelect()
    {
        var path = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-vp9.mkv");
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=25:d=3", "-c:v", "libvpx-vp9", "-g", "25", "-keyint_min", "25", "-deadline", "realtime", path]);
        try
        {
            var pages = await ScanAsync(new QueuedEpisode { EpisodeId = Guid.NewGuid(), Path = path }, 0, 28);

            Assert.Equal([0.0, 1.0, 2.0], pages.Select(page => page.Frame.Time));
            Assert.Equal(await KeyframeFilterOracle.PagesAsync(path, 0, 28, selectKeyframes: true), pages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A transport stream that changes mid-way, so ffmpeg rebuilds the filter graph and signalstats
    /// starts over: the first frame after the change has a difference of 0. A new size reaches the
    /// pipe at that size. A new bit depth at the same size looks the same on the pipe, so only the
    /// log's order tells the reader to start over.
    /// </summary>
    /// <param name="secondSize">The size of the stream's second half.</param>
    /// <param name="secondFormat">The pixel format of the stream's second half.</param>
    [TheorySkipFFmpegTests]
    [InlineData("128x72", "yuv420p")]
    [InlineData("160x90", "yuv420p10le")]
    public async Task MidStreamGraphRebuild_MatchesTheFilters(string secondSize, string secondFormat)
    {
        var first = await EncodeAsync(".ts", "160x90", "yuv420p", "tv", "libx264");
        var second = await EncodeAsync(".ts", secondSize, secondFormat, "tv", "libx264");
        var joined = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-joined.ts");
        await File.WriteAllBytesAsync(joined, [.. await File.ReadAllBytesAsync(first), .. await File.ReadAllBytesAsync(second)]);
        try
        {
            var pages = await ScanAsync(new QueuedEpisode { EpisodeId = Guid.NewGuid(), Path = joined }, 0, 28);
            var expected = await KeyframeFilterOracle.PagesAsync(joined, 0, 28);

            Assert.Equal(expected, pages);
            Assert.Equal(0, pages[0].Visual!.FrameDifference);
            Assert.Equal(2, pages.Count(page => page.Visual!.FrameDifference == 0));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
            File.Delete(joined);
        }
    }

    /// <summary>
    /// ffmpeg logs a file's tags and chapter titles before the first time base and, unless told
    /// otherwise, again after it, among the real headers. Read as a header there, this title would
    /// shift every later page's time.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task ATagThatReadsLikeAHeader_AnnouncesNoFrame()
    {
        const string Tag = "config in time_base: 1/1 n: 0 pts: 77 pts_time:0 fmt:yuv420p cl:left sar:1/1 s:30000x30000 i:P iskey:1 type:I ";
        var path = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + "-tagged.mkv");
        var metadata = Path.ChangeExtension(path, ".txt");
        await File.WriteAllLinesAsync(metadata, [";FFMETADATA1", "title=" + Tag, "[CHAPTER]", "TIMEBASE=1/1000", "START=0", "END=3000", "title=" + Tag]);
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            ["-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x36:r=1:d=3", "-f", "ffmetadata", "-i", metadata, "-map", "0", "-map_metadata", "1", "-map_chapters", "1", "-c:v", "ffv1", "-g", "1", path]);
        try
        {
            var pages = await ScanAsync(new QueuedEpisode { EpisodeId = Guid.NewGuid(), Path = path }, 0, 28);

            Assert.Equal([0.0, 1.0, 2.0], pages.Select(page => page.Frame.Time));
            Assert.Equal(await KeyframeFilterOracle.PagesAsync(path, 0, 28), pages);
        }
        finally
        {
            File.Delete(metadata);
            File.Delete(path);
        }
    }

    // A window end past every keyframe, so no page is clipped.
    private static async Task<KeyframePage[]> ScanAsync(QueuedEpisode episode, double start, int threshold)
    {
        episode.CreditsFingerprintStart = start;
        episode.CreditsFingerprintEnd = 100000;
        return await FfmpegTestHelpers.CreateFFmpegService().ScanKeyframesAsync(episode, new PluginConfiguration { BlackFrameThreshold = threshold });
    }

    // Four seconds of a moving pattern with a keyframe per second, so frames differ and chroma
    // varies. Three frames per second give times a double cannot hold exactly.
    private static async Task<string> EncodeAsync(string extension, string size, string pixelFormat, string range, string encoder)
    {
        var path = DatabaseTestHelpers.CreateTempDbPath(Guid.NewGuid().ToString("N") + extension);
        await new FFmpegProcessRunner(NullLogger.Instance).RunAsync(
            "ffmpeg",
            [
                "-y", "-v", "error",
                "-f", "lavfi", "-i", "testsrc2=s=320x180:r=3:d=4",
                "-vf", "drawbox=c=black:t=fill:h=ih/2,scale=" + size.Replace('x', ':'),
                "-pix_fmt", pixelFormat, "-color_range", range,
                "-c:v", encoder, "-g", "3",
                path,
            ]);
        return path;
    }
}
