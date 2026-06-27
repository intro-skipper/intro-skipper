// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

/* End-to-end spike for subtitle-driven recap detection (RFC A).
 *
 * Unlike the pure tests in TestSubtitleRecapDetection, this test exercises the real ffmpeg
 * extraction path: it muxes a synthetic clip with an embedded TEXT subtitle stream plus a
 * fade-to-black, then runs the exact production code (SubtitleProbe -> SubtitleParser ->
 * FFmpegOutputParser -> SubtitleRecapSegmentBuilder) over genuine ffmpeg output.
 *
 * Gated by FactSkipFFmpegTests so it runs where ffmpeg is on PATH and is skipped otherwise.
 */

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IntroSkipper.FFmpeg;
using IntroSkipper.Subtitles;
using Xunit;

public class TestSubtitleRecapSpike
{
    [FactSkipFFmpegTests]
    public async Task EndToEnd_ExtractsSubtitlesAndBuildsRecapSegment()
    {
        var work = Path.Combine(Path.GetTempPath(), "is-recap-spike-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var srtPath = Path.Combine(work, "recap.srt");
            var videoPath = Path.Combine(work, "video.mp4");
            var episodePath = Path.Combine(work, "episode.mkv");

            await File.WriteAllTextAsync(
                srtPath,
                "1\n00:00:02,000 --> 00:00:05,000\nPreviously on Test Show...\n\n"
                + "2\n00:00:05,500 --> 00:00:09,000\n...the hero lost everything.\n\n"
                + "3\n00:00:30,000 --> 00:00:33,000\nWelcome back. Let's begin.\n");

            // 1) Synthesize a 40s clip with full-black frames in [10,11] (the recap fade-out).
            //    Commas in the enable() expression are backslash-escaped (no shell involved).
            await RunAsync("ffmpeg", new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=5:duration=40",
                "-vf", @"drawbox=x=0:y=0:w=in_w:h=in_h:color=black:t=fill:enable=between(t\,10\,11)",
                "-pix_fmt", "yuv420p", videoPath,
            });

            // 1b) Mux the .srt as an embedded TEXT subtitle stream tagged eng.
            await RunAsync("ffmpeg", new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-i", videoPath, "-i", srtPath,
                "-c:v", "copy", "-c:s", "srt", "-metadata:s:s:0", "language=eng",
                episodePath,
            });

            Assert.True(File.Exists(episodePath), "ffmpeg failed to produce the muxed sample");

            // 2) Enumerate subtitle streams with ffprobe and classify via production code.
            var probeJson = await RunAsync("ffprobe", new[]
            {
                "-v", "error", "-select_streams", "s",
                "-show_entries", "stream=index,codec_name,codec_type,disposition:stream_tags=language",
                "-of", "json", episodePath,
            });

            var streams = SubtitleProbe.Parse(probeJson.StdOut);
            var textStream = Assert.Single(streams, s => s.IsTextBased);
            Assert.Equal("subrip", textStream.Codec);
            Assert.Equal("eng", textStream.Language);

            // 3) Extract ONLY the opening 15s as SubRip to stdout, then parse with production code.
            var extract = await RunAsync("ffmpeg", new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-i", episodePath, "-to", "15",
                "-map", "0:s:" + (streams.Count == 1 ? "0" : textStream.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "-f", "srt", "-",
            });

            var cues = SubtitleParser.ParseSubRip(extract.StdOut);
            Assert.Equal(2, cues.Count); // the 30s cue is correctly excluded by the 15s window
            Assert.True(RecapPhraseMatcher.Default.IsRecapOpening(cues[0].Text));

            // 4) Detect black frames with the SAME filter/parser the plugin uses.
            var black = await RunAsync("ffmpeg", new[]
            {
                "-hide_banner", "-loglevel", "info",
                "-ss", "0", "-i", episodePath, "-to", "20",
                "-an", "-dn", "-sn",
                "-vf", "blackframe=amount=50:threshold=28",
                "-f", "null", "-",
            });

            var blackFrames = FFmpegOutputParser.ParseBlackFrames(black.StdErr);
            Assert.NotEmpty(blackFrames);
            var blackTimes = blackFrames.Select(static f => f.Time).ToList();
            Assert.Contains(blackTimes, t => Math.Abs(t - 10.0) < 0.25);

            // 5) Run the real builder over genuinely extracted cues + black frames.
            var result = SubtitleRecapSegmentBuilder.Build(
                cues,
                RecapPhraseMatcher.Default,
                new SubtitleRecapOptions(),
                blackTimes);

            Assert.NotNull(result);
            Assert.InRange(result!.Start, 1.9, 2.1);   // anchored to the matched cue, NOT 0
            Assert.InRange(result.End, 9.9, 10.1);      // snapped to the 10s fade-to-black
            Assert.True(result.SnappedToBlackFrame);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    private static async Task<(string StdOut, string StdErr)> RunAsync(string fileName, string[] args)
    {
        var info = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {fileName}");

        // Read both streams concurrently to avoid pipe-buffer deadlock.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        await process.WaitForExitAsync();

        return (stdOut, stdErr);
    }
}
