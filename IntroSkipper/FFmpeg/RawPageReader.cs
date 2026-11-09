// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using IntroSkipper.Data;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Reads the raw yuv420p pipe of a keyframe scan: stdout carries the frames, and showinfo on stderr
/// says how big each one is, since a stream can change size mid-way.
/// </summary>
/// <remarks>
/// Each stream blocks on its own below-normal-priority thread: async reads measured a third more
/// CPU, and both are per-frame work inside the server's process.
/// A protocol error (a short frame, or bytes no header announced) kills ffmpeg first and only then
/// throws <see cref="InvalidDataException"/>, because ffmpeg blocks on a full pipe once nobody reads
/// it and would never close stderr.
/// </remarks>
internal static partial class RawPageReader
{
    // A longer run without a newline is cut into pieces. Headers are far shorter.
    private const int LogLineMaximumBytes = 64 * 1024;

    // Where the default ProcessPriority puts the ffmpeg child.
    private const int LinuxNiceBelowNormal = 10;

    // How long a frame's bytes wait for its header. ffmpeg logs the header before it writes the
    // frame, so the wait only covers the log thread getting scheduled. Past it no header is coming.
    private static readonly TimeSpan _headerWait = TimeSpan.FromSeconds(10);

    private static readonly byte[][] _sliceStructureErrors = [.. FFmpegService.SliceStructureErrors.Select(Encoding.ASCII.GetBytes)];

    /// <summary>
    /// Reads both output streams of a keyframe scan to their ends.
    /// </summary>
    /// <param name="stdout">The raw frames.</param>
    /// <param name="stderr">ffmpeg's log at info level.</param>
    /// <param name="kill">Kills ffmpeg, which ends both streams.</param>
    /// <param name="threshold">The luma under which a pixel counts as black.</param>
    /// <returns>The pages read, and whether the log held one of <see cref="FFmpegService.SliceStructureErrors"/>. A run that fails on a frame boundary, a nonzero exit included, gives the pages read so far.</returns>
    /// <exception cref="InvalidDataException">The streams broke the protocol; ffmpeg has been killed.</exception>
    public static async Task<(KeyframePage[] Pages, bool SliceStructureError)> ReadAsync(Stream stdout, Stream stderr, Action kill, int threshold)
    {
        using var headers = new BlockingCollection<FrameHeader>();
        var log = RunBelowNormal("IntroSkipper keyframe log", () => ReadLog(stderr, headers), kill);
        var frames = RunBelowNormal("IntroSkipper keyframe pages", () => ReadFrames(stdout, headers, threshold), kill);

        // Both tasks end: a fault on either side kills ffmpeg, which closes the other stream.
        await Task.WhenAll(log, frames).ConfigureAwait(false);
        return (await frames.ConfigureAwait(false), await log.ConfigureAwait(false));
    }

    // Thread.Priority never reaches a Linux thread: the runtime only calls pthread_setschedparam,
    // whose range under SCHED_OTHER is 0. Nice is per thread on Linux, and who = 0 is the caller.
    private static Task<T> RunBelowNormal<T>(string name, Func<T> work, Action kill)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    _ = NativeMethods.setpriority(NativeMethods.PrioProcess, 0, LinuxNiceBelowNormal);
                }

                result.SetResult(work());
            }
            catch (Exception ex)
            {
                // An exception left on this thread would take the server down with it.
                kill();
                result.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.BelowNormal,
        }.Start();
        return result.Task;
    }

    private static KeyframePage[] ReadFrames(Stream stdout, BlockingCollection<FrameHeader> headers, int threshold)
    {
        var pages = new List<KeyframePage>();
        byte[] frame = [];
        byte[] previous = [];
        Span<byte> firstByte = stackalloc byte[1];
        try
        {
            // The stream decides where the scan ends, not the log: a header without a frame is
            // no page, and bytes without a header are an error, found here while ffmpeg may still
            // be blocked writing them.
            while (stdout.Read(firstByte) > 0)
            {
                if (!headers.TryTake(out var header, _headerWait))
                {
                    throw new InvalidDataException("ffmpeg wrote bytes past the frames its log announced");
                }

                var size = (header.Width * header.Height) + (2 * ((header.Width + 1) / 2) * ((header.Height + 1) / 2));
                if (frame.Length < size)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(size);
                    ArrayPool<byte>.Shared.Return(frame);
                    frame = larger;
                }

                frame[0] = firstByte[0];
                if (stdout.ReadAtLeast(frame.AsSpan(1, size - 1), size - 1, throwOnEndOfStream: false) < size - 1)
                {
                    throw new InvalidDataException(FormattableString.Invariant($"ffmpeg ended a {header.Width}x{header.Height} frame early"));
                }

                // A frame with a negative or missing time is no page, as blackframe's row for it
                // never parsed, but it is still the frame the next one is compared with.
                if (header.Pts >= 0)
                {
                    ReadOnlySpan<byte> previousLuma = header.Reset ? [] : previous.AsSpan(0, header.Width * header.Height);
                    pages.Add(PageFacts.Measure(frame.AsSpan(0, size), previousLuma, header.Width, header.Height, threshold, header.Time));
                }

                (frame, previous) = (previous, frame);
            }

            return [.. pages];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
            ArrayPool<byte>.Shared.Return(previous);
        }
    }

    // Turns log lines into frame headers. A Dolby Vision frame logs kilobytes of side data, so
    // lines are searched as bytes, and only a line with a header or time base becomes a string.
    private static bool ReadLog(Stream stderr, BlockingCollection<FrameHeader> headers)
    {
        var timeBase = 0.0;
        var reset = false;
        var sliceStructureError = false;
        var buffer = new byte[LogLineMaximumBytes];
        try
        {
            var filled = 0;
            int read;
            while ((read = stderr.Read(buffer.AsSpan(filled))) > 0)
            {
                var rest = buffer.AsSpan(0, filled + read);
                int newline;
                while ((newline = rest.IndexOf((byte)'\n')) >= 0)
                {
                    ReadLine(rest[..newline]);
                    rest = rest[(newline + 1)..];
                }

                if (rest.Length == buffer.Length)
                {
                    ReadLine(rest);
                    rest = [];
                }

                rest.CopyTo(buffer);
                filled = rest.Length;
            }

            ReadLine(buffer.AsSpan(0, filled));
            return sliceStructureError;
        }
        finally
        {
            headers.CompleteAdding();
        }

        void ReadLine(ReadOnlySpan<byte> line)
        {
            if (line.IndexOf("config in time_base:"u8) >= 0
                && TimeBaseRegex().Match(Encoding.Latin1.GetString(line)) is { Success: true } match)
            {
                timeBase = double.Parse(match.Groups["numerator"].ValueSpan, CultureInfo.InvariantCulture)
                    / double.Parse(match.Groups["denominator"].ValueSpan, CultureInfo.InvariantCulture);
                reset = true;
            }

            // The input dump logs a file's tags before the first time base, and one can read like a
            // header. The output dump after it would repeat them, so the scan runs -map_metadata -1.
            if (timeBase > 0
                && line.IndexOf(" pts_time:"u8) >= 0
                && FrameRegex().Match(Encoding.Latin1.GetString(line)) is { Success: true } frame)
            {
                headers.Add(new FrameHeader(
                    long.TryParse(frame.Groups["pts"].ValueSpan, CultureInfo.InvariantCulture, out var pts) ? pts : -1,
                    timeBase,
                    int.Parse(frame.Groups["width"].ValueSpan, CultureInfo.InvariantCulture),
                    int.Parse(frame.Groups["height"].ValueSpan, CultureInfo.InvariantCulture),
                    reset));
                reset = false;
            }

            foreach (var error in _sliceStructureErrors)
            {
                sliceStructureError |= line.IndexOf(error) >= 0;
            }
        }
    }

    [GeneratedRegex(@"config in time_base: (?<numerator>\d+)/(?<denominator>[1-9]\d*)")]
    private static partial Regex TimeBaseRegex();

    // Anywhere in the line: a message from another thread that ends without a newline lands in
    // front of showinfo's fields. Its later fields are the same message, so they stay together.
    [GeneratedRegex(@"n:\s*\d+ pts:\s*(?<pts>\S+) pts_time:.*?fmt:\S+ .*?s:(?<width>\d+)x(?<height>\d+) ")]
    private static partial Regex FrameRegex();

    /// <summary>
    /// What showinfo says about one frame.
    /// </summary>
    /// <param name="Pts">The presentation timestamp, or negative when ffmpeg printed NOPTS.</param>
    /// <param name="TimeBase">Seconds per timestamp unit.</param>
    /// <param name="Width">The frame's width.</param>
    /// <param name="Height">The frame's height.</param>
    /// <param name="Reset">Whether the filter graph was configured since the last frame, which is when signalstats starts over with no frame before.</param>
    private readonly record struct FrameHeader(long Pts, double TimeBase, int Width, int Height, bool Reset)
    {
        // blackframe's "%f" of pts * av_q2d(time_base), parsed back. The interval probe and the
        // cut start key cache rows on these exact bits.
        public double Time => double.Parse((Pts * TimeBase).ToString("F6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static class NativeMethods
    {
        internal const int PrioProcess = 0;

        [DllImport("libc")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int setpriority(int which, uint who, int priority);
    }
}
