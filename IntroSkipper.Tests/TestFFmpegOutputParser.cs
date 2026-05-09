// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only


using System;
using System.Collections.Generic;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegOutputParser
{
    // === ParseSilenceRaw characterization ===

    [Fact]
    public void ParseSilenceRaw_SingleRange_ReturnsOneRange()
    {
        var raw = "silence_start: 1.000\nsilence_end: 2.500\n";
        var result = FFmpegOutputParser.ParseSilenceRaw(raw, 0);

        Assert.Single(result);
        Assert.Equal(1.0, result[0].Start);
        Assert.Equal(2.5, result[0].End);
    }

    [Fact]
    public void ParseSilenceRaw_MultipleRanges_ReturnsAll()
    {
        var raw = "silence_start: 1.0\nsilence_end: 2.5\nsilence_start: 5.0\nsilence_end: 6.3\n";
        var result = FFmpegOutputParser.ParseSilenceRaw(raw, 0);

        Assert.Equal(2, result.Length);
        Assert.Equal(1.0, result[0].Start);
        Assert.Equal(2.5, result[0].End);
        Assert.Equal(5.0, result[1].Start);
        Assert.Equal(6.3, result[1].End);
    }

    [Fact]
    public void ParseSilenceRaw_WithRangeStart_OffsetsTimestamps()
    {
        var raw = "silence_start: 1.0\nsilence_end: 2.5\n";
        var result = FFmpegOutputParser.ParseSilenceRaw(raw, 10.0);

        Assert.Single(result);
        Assert.Equal(11.0, result[0].Start);
        Assert.Equal(12.5, result[0].End);
    }

    [Fact]
    public void ParseSilenceRaw_EmptyString_ReturnsEmpty()
    {
        var result = FFmpegOutputParser.ParseSilenceRaw(string.Empty, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseSilenceRaw_NoMatches_ReturnsEmpty()
    {
        var raw = "some unrelated ffmpeg output\nno silence here\n";
        var result = FFmpegOutputParser.ParseSilenceRaw(raw, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseSilenceRaw_UnpairedStart_ReturnsEmpty()
    {
        // Only a start with no end — no complete range to return
        var raw = "silence_start: 1.0\n";
        var result = FFmpegOutputParser.ParseSilenceRaw(raw, 0);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseSilenceRaw_MixedWithOtherOutput_ParsesCorrectly()
    {
        var raw = "[silencedetect @ 0x0] silence_start: 3.5\n" +
                  "size=N/A time=00:00:10.00\n" +
                  "[silencedetect @ 0x0] silence_end: 4.2 | silence_duration: 0.7\n";
        var result = FFmpegOutputParser.ParseSilenceRaw(raw, 0);

        Assert.Single(result);
        Assert.Equal(3.5, result[0].Start);
        Assert.Equal(4.2, result[0].End);
    }

    // === ParseKeyFramesRaw characterization ===

    [Fact]
    public void ParseKeyFramesRaw_SingleKeyframe_ReturnsOne()
    {
        var raw = "[Parsed_showinfo_0 @ 0x0] n:0 pts:0 pts_time:3.250 pos:0 fmt:yuv420p\n";
        var result = FFmpegOutputParser.ParseKeyFramesRaw(raw, 0, null);

        Assert.Single(result);
        Assert.Equal(3.25, result[0]);
    }

    [Fact]
    public void ParseKeyFramesRaw_MultipleKeyframes_ReturnsAll()
    {
        var raw = "[Parsed_showinfo_0 @ 0x0] n:0 pts:0 pts_time:1.000 pos:0\n" +
                  "[Parsed_showinfo_0 @ 0x0] n:1 pts:3000 pts_time:3.000 pos:12345\n" +
                  "[Parsed_showinfo_0 @ 0x0] n:2 pts:5000 pts_time:5.500 pos:24680\n";
        var result = FFmpegOutputParser.ParseKeyFramesRaw(raw, 0, null);

        Assert.Equal(3, result.Length);
        Assert.Equal(1.0, result[0]);
        Assert.Equal(3.0, result[1]);
        Assert.Equal(5.5, result[2]);
    }

    [Fact]
    public void ParseKeyFramesRaw_WithRangeStart_OffsetsTimestamps()
    {
        var raw = "[Parsed_showinfo_0 @ 0x0] n:0 pts:0 pts_time:2.000 pos:0\n";
        var result = FFmpegOutputParser.ParseKeyFramesRaw(raw, 10.0, null);

        Assert.Single(result);
        Assert.Equal(12.0, result[0]);
    }

    [Fact]
    public void ParseKeyFramesRaw_EmptyString_ReturnsEmpty()
    {
        var result = FFmpegOutputParser.ParseKeyFramesRaw(string.Empty, 0, null);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseKeyFramesRaw_NoPtsTime_ReturnsEmpty()
    {
        var raw = "some unrelated output without pts_time\n";
        var result = FFmpegOutputParser.ParseKeyFramesRaw(raw, 0, null);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseKeyFramesRaw_InvalidTimestamp_SkipsLine()
    {
        // When Logger is null (default in tests), invalid timestamps are silently skipped
        var raw = "[Parsed_showinfo_0 @ 0x0] n:0 pts:0 pts_time:INVALID pos:0\n" +
                  "[Parsed_showinfo_0 @ 0x0] n:1 pts:3000 pts_time:3.000 pos:12345\n";
        var result = FFmpegOutputParser.ParseKeyFramesRaw(raw, 0, null);

        Assert.Single(result);
        Assert.Equal(3.0, result[0]);
    }

    [Fact]
    public void ParseKeyFramesRaw_InvalidTimestamp_LogsWarningWhenLoggerProvided()
    {
        var logger = new CapturingLogger();
        var raw = "[Parsed_showinfo_0 @ 0x0] n:0 pts:0 pts_time:INVALID pos:0\n" +
                  "[Parsed_showinfo_0 @ 0x0] n:1 pts:3000 pts_time:3.000 pos:12345\n";

        var result = FFmpegOutputParser.ParseKeyFramesRaw(raw, 0, logger);

        Assert.Single(result);
        Assert.Equal(3.0, result[0]);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Failed to parse timestamp", warning.Message, StringComparison.Ordinal);
        Assert.Contains("INVALID", warning.Message, StringComparison.Ordinal);
    }

    // === ParseBlackFrames characterization (complement existing fixture tests) ===

    [Fact]
    public void ParseBlackFrames_SingleFrame_ReturnsOne()
    {
        var raw = "[Parsed_blackframe_0 @ 0x0000000] frame:1 pblack:99 pts:43 t:0.043000 type:B last_keyframe:0\n";
        var result = FFmpegOutputParser.ParseBlackFrames(raw);

        Assert.Single(result);
        Assert.Equal(99, result[0].Percentage);
        Assert.Equal(0.043, result[0].Time);
        Assert.Equal(1, result[0].Frame);
    }

    [Fact]
    public void ParseBlackFrames_EmptyString_ReturnsEmpty()
    {
        var result = FFmpegOutputParser.ParseBlackFrames(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseBlackFrames_NoMatches_ReturnsEmpty()
    {
        var raw = "some unrelated ffmpeg output\n";
        var result = FFmpegOutputParser.ParseBlackFrames(raw);
        Assert.Empty(result);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
