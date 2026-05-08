// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestFFmpegCapabilityService
{
    [Fact]
    public void CheckFFmpegVersion_RunsRequirementsOnEveryCall()
    {
        var runner = new CapabilityRunner();
        var service = new FFmpegCapabilityService(runner, NullLogger<FFmpegCapabilityService>.Instance);

        Assert.True(service.CheckFFmpegVersion());
        Assert.True(service.CheckFFmpegVersion());

        Assert.Equal(8, runner.Calls.Count);
        Assert.Equal(2, runner.Calls.Count(call => call == "-version"));
        Assert.Equal(2, runner.Calls.Count(call => call == "-muxers"));
        Assert.Equal(2, runner.Calls.Count(call => call == "-h muxer=chromaprint"));
        Assert.Equal(2, runner.Calls.Count(call => call == "-h filter=silencedetect"));
    }

    [Fact]
    public void GetChromaprintLogs_OrdersKnownDiagnosticsDeterministically()
    {
        var service = new FFmpegCapabilityService(new CapabilityRunner(), NullLogger<FFmpegCapabilityService>.Instance);

        Assert.True(service.CheckFFmpegVersion());

        var logs = service.GetChromaprintLogs();
        Assert.True(logs.IndexOf("FFmpeg version:", StringComparison.Ordinal) < logs.IndexOf("FFmpeg muxer list:", StringComparison.Ordinal));
        Assert.True(logs.IndexOf("FFmpeg muxer list:", StringComparison.Ordinal) < logs.IndexOf("FFmpeg chromaprint options:", StringComparison.Ordinal));
        Assert.True(logs.IndexOf("FFmpeg chromaprint options:", StringComparison.Ordinal) < logs.IndexOf("FFmpeg silencedetect options:", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckFFmpegVersion_RechecksAfterFailure()
    {
        WarningManager.Clear();
        var runner = new CapabilityRunner { FailFirstVersionCheck = true };
        var service = new FFmpegCapabilityService(runner, NullLogger<FFmpegCapabilityService>.Instance);

        try
        {
            Assert.False(service.CheckFFmpegVersion());
            Assert.True(service.CheckFFmpegVersion());

            Assert.Equal(5, runner.Calls.Count);
            Assert.Equal(2, runner.Calls.Count(call => call == "-version"));
        }
        finally
        {
            WarningManager.Clear();
        }
    }

    private sealed class CapabilityRunner : IFFmpegRunner
    {
        private int _versionChecks;

        public bool FailFirstVersionCheck { get; init; }

        public List<string> Calls { get; } = [];

        public FFmpegProcessResult Run(IReadOnlyList<string> args, bool stderr = false, int timeout = 60 * 1000)
        {
            var key = string.Join(" ", args);
            Calls.Add(key);

            if (FailFirstVersionCheck && key == "-version" && _versionChecks++ == 0)
            {
                return CreateResult("not a supported build");
            }

            return key switch
            {
                "-version" => CreateResult("ffmpeg version test"),
                "-muxers" => CreateResult("chromaprint"),
                "-h muxer=chromaprint" => CreateResult("binary raw fingerprint"),
                "-h filter=silencedetect" => CreateResult("noise tolerance"),
                _ => CreateResult(string.Empty)
            };
        }

        public Task<FFmpegProcessResult> RunAsync(
            IReadOnlyList<string> args,
            bool stderr = false,
            int timeout = 60 * 1000,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static FFmpegProcessResult CreateResult(string output)
            => new(Encoding.UTF8.GetBytes(output), 0);
    }
}
