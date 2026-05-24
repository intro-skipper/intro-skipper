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
    public async Task CheckFFmpegVersion_CachesSuccessfulResult()
    {
        var runner = new CapabilityRunner();
        var service = new FFmpegCapabilityService(runner, NullLogger<FFmpegCapabilityService>.Instance);

        Assert.True(await service.CheckFFmpegVersionAsync().ConfigureAwait(true));
        Assert.True(await service.CheckFFmpegVersionAsync().ConfigureAwait(true));

        // Second call should be served from cache, only 4 subprocess invocations total.
        Assert.Equal(4, runner.Calls.Count);
        Assert.Single(runner.Calls, call => call == "-version");
        Assert.Single(runner.Calls, call => call == "-muxers");
        Assert.Single(runner.Calls, call => call == "-h muxer=chromaprint");
        Assert.Single(runner.Calls, call => call == "-h filter=silencedetect");
    }

    [Fact]
    public async Task GetChromaprintLogs_OrdersKnownDiagnosticsDeterministically()
    {
        var service = new FFmpegCapabilityService(new CapabilityRunner(), NullLogger<FFmpegCapabilityService>.Instance);

        Assert.True(await service.CheckFFmpegVersionAsync().ConfigureAwait(true));

        var logs = service.GetChromaprintLogs();
        Assert.True(logs.IndexOf("FFmpeg version:", StringComparison.Ordinal) < logs.IndexOf("FFmpeg muxer list:", StringComparison.Ordinal));
        Assert.True(logs.IndexOf("FFmpeg muxer list:", StringComparison.Ordinal) < logs.IndexOf("FFmpeg chromaprint options:", StringComparison.Ordinal));
        Assert.True(logs.IndexOf("FFmpeg chromaprint options:", StringComparison.Ordinal) < logs.IndexOf("FFmpeg silencedetect options:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckFFmpegVersion_RechecksAfterFailure()
    {
        WarningManager.Clear();
        var runner = new CapabilityRunner { FailFirstVersionCheck = true };
        var service = new FFmpegCapabilityService(runner, NullLogger<FFmpegCapabilityService>.Instance);

        try
        {
            Assert.False(await service.CheckFFmpegVersionAsync().ConfigureAwait(true));
            Assert.True(await service.CheckFFmpegVersionAsync().ConfigureAwait(true));

            Assert.Equal(5, runner.Calls.Count);
            Assert.Equal(2, runner.Calls.Count(call => call == "-version"));
            Assert.Equal("None", WarningManager.GetWarnings());
        }
        finally
        {
            WarningManager.Clear();
        }
    }

    [Fact]
    public async Task CheckFFmpegVersion_Timeout_ReturnsFalseAndSetsWarning()
    {
        WarningManager.Clear();
        var runner = new CapabilityRunner { VersionResult = FFmpegProcessStatus.TimedOut };
        var service = new FFmpegCapabilityService(runner, NullLogger<FFmpegCapabilityService>.Instance);

        try
        {
            var result = await service.CheckFFmpegVersionAsync().ConfigureAwait(true);

            Assert.False(result);
            Assert.Contains("IncompatibleFFmpegBuild", WarningManager.GetWarnings(), StringComparison.Ordinal);
            AssertIncompatibleVersionDiagnostic(service, "ffmpeg version test");
        }
        finally
        {
            WarningManager.Clear();
        }
    }

    [Fact]
    public async Task CheckFFmpegVersion_NonzeroExit_ReturnsFalseAndSetsWarning()
    {
        WarningManager.Clear();
        var runner = new CapabilityRunner { VersionExitCode = 1 };
        var service = new FFmpegCapabilityService(runner, NullLogger<FFmpegCapabilityService>.Instance);

        try
        {
            var result = await service.CheckFFmpegVersionAsync().ConfigureAwait(true);

            Assert.False(result);
            Assert.Contains("IncompatibleFFmpegBuild", WarningManager.GetWarnings(), StringComparison.Ordinal);
            AssertIncompatibleVersionDiagnostic(service, "ffmpeg version test");
        }
        finally
        {
            WarningManager.Clear();
        }
    }

    [Fact]
    public async Task CheckFFmpegVersion_MissingRequiredOutput_ReturnsFalseAndSetsWarning()
    {
        WarningManager.Clear();
        var runner = new CapabilityRunner { VersionOutput = "unexpected garbage output" };
        var service = new FFmpegCapabilityService(runner, NullLogger<FFmpegCapabilityService>.Instance);

        try
        {
            var result = await service.CheckFFmpegVersionAsync().ConfigureAwait(true);

            Assert.False(result);
            Assert.Contains("IncompatibleFFmpegBuild", WarningManager.GetWarnings(), StringComparison.Ordinal);
            AssertIncompatibleVersionDiagnostic(service, "unexpected garbage output");
        }
        finally
        {
            WarningManager.Clear();
        }
    }

    private static void AssertIncompatibleVersionDiagnostic(FFmpegCapabilityService service, string expectedVersionOutput)
    {
        var logs = service.GetChromaprintLogs();

        Assert.Contains("* FFmpeg: `unknown_error`", logs, StringComparison.Ordinal);
        Assert.Contains("FFmpeg version:", logs, StringComparison.Ordinal);
        Assert.Contains(expectedVersionOutput, logs, StringComparison.Ordinal);
    }

    private sealed class CapabilityRunner : IFFmpegRunner
    {
        private int _versionChecks;

        public bool FailFirstVersionCheck { get; init; }

        public FFmpegProcessStatus VersionResult { get; init; } = FFmpegProcessStatus.Completed;

        public int VersionExitCode { get; init; }

        public string? VersionOutput { get; init; }

        public List<string> Calls { get; } = [];

        public Task<FFmpegProcessResult> RunAsync(
            IReadOnlyList<string> args,
            FFmpegOutputStream outputStream = FFmpegOutputStream.Stdout,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var key = string.Join(" ", args);
            Calls.Add(key);

            if (key == "-version")
            {
                if (FailFirstVersionCheck && _versionChecks++ == 0)
                {
                    return Task.FromResult(CreateResult("not a supported build"));
                }

                if (VersionResult != FFmpegProcessStatus.Completed || VersionExitCode != 0 || VersionOutput is not null)
                {
                    var output = VersionOutput ?? "ffmpeg version test";
                    return Task.FromResult(new FFmpegProcessResult(
                        Encoding.UTF8.GetBytes(output),
                        Array.Empty<byte>(),
                        VersionResult,
                        VersionResult == FFmpegProcessStatus.Completed ? VersionExitCode : null));
                }
            }

            return Task.FromResult(key switch
            {
                "-version" => CreateResult("ffmpeg version test"),
                "-muxers" => CreateResult("chromaprint"),
                "-h muxer=chromaprint" => CreateResult("binary raw fingerprint"),
                "-h filter=silencedetect" => CreateResult("noise tolerance"),
                _ => CreateResult(string.Empty)
            });
        }

        private static FFmpegProcessResult CreateResult(string output)
            => new(Encoding.UTF8.GetBytes(output), Array.Empty<byte>(), FFmpegProcessStatus.Completed, 0);
    }
}
