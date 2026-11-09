// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// The pipe's protocol against a fake ffmpeg whose two streams stay open until it exits or is
/// killed, as a real one blocked on a full pipe does.
/// </summary>
public sealed class TestRawPageReader
{
    private const string TimeBase = "[Parsed_showinfo_1 @ 0x1] config in time_base: 1/1000, frame_rate: 25/1\n";

    /// <summary>
    /// The second frame is half the size of the first. A reader that kept the first size would
    /// run off the stream. The values are worked by hand: a luma of 0 is under the threshold, and a
    /// chroma pair 3 and 4 from neutral has a saturation of 5.
    /// </summary>
    [Fact]
    public async Task ReadsEachFrameAtTheSizeItsHeaderAnnounces()
    {
        using var ffmpeg = new FakeFfmpeg();
        var read = ffmpeg.ReadAsync();

        ffmpeg.Log(TimeBase + Header(1000, "4x2"));
        ffmpeg.WriteFrame(4, 2, luma: 0, u: 128, v: 128);
        ffmpeg.Log("[Parsed_showinfo_1 @ 0x1] config in time_base: 1/2, frame_rate: 25/1\n" + Header(3, "2x2"));
        ffmpeg.WriteFrame(2, 2, luma: 100, u: 131, v: 132);
        ffmpeg.Exit();

        var pages = (await read).Pages;
        Assert.Equal([1.0, 1.5], pages.Select(page => page.Frame.Time));
        Assert.Equal([100, 0], pages.Select(page => page.Frame.Percentage));
        Assert.Equal([0.0, 5.0], pages.Select(page => page.Visual!.Saturation));
        Assert.False(ffmpeg.Killed);
    }

    /// <summary>
    /// The difference compares consecutive frames, and a frame with no usable time still takes its
    /// turn: blackframe's row for it never parsed, but signalstats saw it.
    /// </summary>
    [Theory]
    [InlineData("NOPTS")]
    [InlineData("-40")]
    public async Task AFrameWithoutATimeIsNoPageButTheNextComparesWithIt(string pts)
    {
        using var ffmpeg = new FakeFfmpeg();
        var read = ffmpeg.ReadAsync();

        ffmpeg.Log(TimeBase + Header(0, "4x2"));
        ffmpeg.WriteFrame(4, 2, luma: 10, u: 128, v: 128);
        ffmpeg.Log(Header(pts, "4x2"));
        ffmpeg.WriteFrame(4, 2, luma: 20, u: 128, v: 128);
        ffmpeg.Log(Header(80, "4x2"));
        ffmpeg.WriteFrame(4, 2, luma: 50, u: 128, v: 128);
        ffmpeg.Exit();

        var pages = (await read).Pages;
        Assert.Equal([0.0, 0.08], pages.Select(page => page.Frame.Time));
        Assert.Equal([0.0, 30.0], pages.Select(page => page.Visual!.FrameDifference));
    }

    /// <summary>
    /// A message from another thread can land in front of showinfo's fields on the same line, and
    /// a line can reach the reader in pieces.
    /// </summary>
    [Fact]
    public async Task FindsAHeaderBehindAnotherMessageAndAcrossReads()
    {
        using var ffmpeg = new FakeFfmpeg();
        var read = ffmpeg.ReadAsync();

        const string Message = "[hevc @ 0x1] Multiple Dolby Vision RPUs found in one AU. ";
        var line = Message + TimeBase + Message + Header(0, "4x2");
        foreach (var piece in line.Chunk(7))
        {
            ffmpeg.Log(new string(piece));
        }

        ffmpeg.WriteFrame(4, 2, luma: 10, u: 128, v: 128);
        ffmpeg.Exit();

        Assert.Single((await read).Pages);
    }

    /// <summary>
    /// ffmpeg on Windows ends its log lines in CR LF, and no cut at a carriage return may cost a line its header.
    /// </summary>
    [Fact]
    public async Task ReadsAHeaderFromALineEndingInCrLf()
    {
        using var ffmpeg = new FakeFfmpeg();
        var read = ffmpeg.ReadAsync();

        ffmpeg.Log((TimeBase + Header(1000, "4x2")).Replace("\n", "\r\n", StringComparison.Ordinal));
        ffmpeg.WriteFrame(4, 2, luma: 10, u: 128, v: 128);
        ffmpeg.Exit();

        Assert.Equal(1.0, Assert.Single((await read).Pages).Frame.Time);
    }

    /// <summary>
    /// Page count comes from the frames read: a run that stops on a frame boundary, as a failed
    /// run can after logging a header it never wrote, keeps the pages it has.
    /// </summary>
    [Fact]
    public async Task AHeaderWithoutAFrameIsNoPage()
    {
        using var ffmpeg = new FakeFfmpeg();
        var read = ffmpeg.ReadAsync();

        ffmpeg.Log(TimeBase + Header(0, "4x2"));
        ffmpeg.WriteFrame(4, 2, luma: 10, u: 128, v: 128);
        ffmpeg.Log(Header(40, "4x2"));
        ffmpeg.Exit();

        Assert.Single((await read).Pages);
        Assert.False(ffmpeg.Killed);
    }

    /// <summary>
    /// Bytes no header announced. ffmpeg, blocked on the pipe, keeps both streams open, so a reader
    /// that waits for stderr to end before it looks at the rest of stdout never finishes.
    /// </summary>
    [Fact]
    public async Task KillsFfmpegOnBytesNoHeaderAnnounced()
    {
        using var ffmpeg = new FakeFfmpeg();
        var read = ffmpeg.ReadAsync();

        ffmpeg.Log(TimeBase + Header(0, "4x2"));
        ffmpeg.WriteFrame(4, 2, luma: 10, u: 128, v: 128);
        ffmpeg.Write(new byte[1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => read);
        Assert.True(ffmpeg.Killed);
    }

    [Fact]
    public async Task KillsFfmpegOnAShortFrame()
    {
        using var ffmpeg = new FakeFfmpeg();
        var read = ffmpeg.ReadAsync();

        ffmpeg.Log(TimeBase + Header(0, "4x2"));
        ffmpeg.Write(new byte[5]);
        ffmpeg.CloseStdout();

        await Assert.ThrowsAsync<InvalidDataException>(() => read);
        Assert.True(ffmpeg.Killed);
    }

    /// <summary>
    /// A run of log longer than the read buffer, with no newline, is read in pieces, and the
    /// header after it still counts. A reader that stopped at a full buffer would leave ffmpeg
    /// blocked on its log.
    /// </summary>
    [Fact]
    public async Task ReadsOnPastALogLineLongerThanItsBuffer()
    {
        using var ffmpeg = new FakeFfmpeg();
        var read = ffmpeg.ReadAsync();

        ffmpeg.Log(new string('x', 200 * 1024) + "\n" + TimeBase + Header(0, "4x2"));
        ffmpeg.WriteFrame(4, 2, luma: 10, u: 128, v: 128);
        ffmpeg.Exit();

        Assert.Single((await read).Pages);
    }

    /// <summary>
    /// Both streams are read per frame inside the server's process, so neither may compete with its
    /// request threads. Thread.Priority never reaches a Linux thread, so there the nice value counts.
    /// </summary>
    [Fact]
    public async Task ReadsBothStreamsBelowNormalPriority()
    {
        using var stdout = new PriorityRecordingStream();
        using var stderr = new PriorityRecordingStream();

        await RawPageReader.ReadAsync(stdout, stderr, () => { }, 16);

        Assert.Equal(ThreadPriority.BelowNormal, stdout.Priority);
        Assert.Equal(ThreadPriority.BelowNormal, stderr.Priority);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(10, stdout.Nice);
            Assert.Equal(10, stderr.Nice);
        }
    }

    /// <summary>
    /// A kill that lands inside a frame reads as a short frame. That is the kill's doing, so the
    /// cancellation or timeout that caused it is what the caller hears.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task AReadThatFaultsAfterTheKillLosesToTheTimeout()
    {
        var run = new FFmpegProcessRunner(new CapturingLogger<TestRawPageReader>()).RunStreamingAsync<int>(
            "ffmpeg",
            ["-hide_banner", "-loglevel", "info", "-re", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=1", "-f", "null", "-"],
            async (stdout, stderr, kill) =>
            {
                await Task.WhenAll(stdout.CopyToAsync(Stream.Null), stderr.CopyToAsync(Stream.Null));
                throw new InvalidDataException("short frame");
            },
            timeout: 500);

        await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(60)));
    }

    private static string Header(int pts, string size) => Header(pts.ToString(System.Globalization.CultureInfo.InvariantCulture), size);

    // showinfo's line at checksum=0, on jellyfin-ffmpeg 8.1.2.
    private static string Header(string pts, string size)
        => $"[Parsed_showinfo_1 @ 0x1] n:   0 pts:{pts,7} pts_time:0       duration:    500 duration_time:0.5     fmt:yuv420p cl:left sar:1/1 s:{size} i:P iskey:1 type:I \n";

    // An empty stream that records the thread its first read blocks on.
    private sealed class PriorityRecordingStream : MemoryStream
    {
        public ThreadPriority Priority { get; private set; }

        public int Nice { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            Priority = Thread.CurrentThread.Priority;
            if (OperatingSystem.IsLinux())
            {
                // Field 19 of proc(5), the 17th after the thread name, which may hold spaces.
                var stat = File.ReadAllText("/proc/thread-self/stat");
                Nice = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[16], System.Globalization.CultureInfo.InvariantCulture);
            }

            return 0;
        }
    }

    private sealed class FakeFfmpeg : IDisposable
    {
        // No back-pressure: a pending write would never return once the reader stops reading.
        private readonly Pipe _stdout = new(new PipeOptions(pauseWriterThreshold: 0));
        private readonly Pipe _stderr = new(new PipeOptions(pauseWriterThreshold: 0));
        private readonly Lock _gate = new();

        public bool Killed { get; private set; }

        public Task<(KeyframePage[] Pages, bool SliceStructureError)> ReadAsync()
            => RawPageReader.ReadAsync(_stdout.Reader.AsStream(), _stderr.Reader.AsStream(), Kill, 16)
                .WaitAsync(TimeSpan.FromSeconds(60));

        public void Log(string text) => Send(_stderr, Encoding.ASCII.GetBytes(text));

        public void Write(byte[] bytes) => Send(_stdout, bytes);

        public void WriteFrame(int width, int height, byte luma, byte u, byte v)
        {
            var chroma = ((width + 1) / 2) * ((height + 1) / 2);
            Write(
            [
                .. Enumerable.Repeat(luma, width * height),
                .. Enumerable.Repeat(u, chroma),
                .. Enumerable.Repeat(v, chroma),
            ]);
        }

        public void CloseStdout()
        {
            lock (_gate)
            {
                _stdout.Writer.Complete();
            }
        }

        public void Exit()
        {
            lock (_gate)
            {
                _stdout.Writer.Complete();
                _stderr.Writer.Complete();
            }
        }

        public void Dispose() => Exit();

        private void Kill()
        {
            lock (_gate)
            {
                Killed = true;
                _stdout.Writer.Complete();
                _stderr.Writer.Complete();
            }
        }

        // Writes after the kill go nowhere, like a real process's after it dies.
        private void Send(Pipe pipe, byte[] bytes)
        {
            lock (_gate)
            {
                if (!Killed)
                {
                    pipe.Writer.Write(bytes);
                    _ = pipe.Writer.FlushAsync();
                }
            }
        }
    }
}
