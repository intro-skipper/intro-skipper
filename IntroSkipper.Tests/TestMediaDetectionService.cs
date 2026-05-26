// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// Characterization tests for <see cref="MediaDetectionService"/> orchestration:
/// argument construction, stream selection, timeout semantics, cache integration,
/// and binary vs text output path selection.
/// </summary>
public class TestMediaDetectionService
{
    private static readonly byte[] FourByteFingerprint = BitConverter.GetBytes(42u);

    // === FingerprintAsync: binary stdout path ===

    [Fact]
    public async Task FingerprintAsync_Arguments_ContainExpectedTokensInOrder()
    {
        var episode = CreateEpisode(introEnd: 60);
        IReadOnlyList<string>? captured = null;

        var svc = CreateService(
            runner: (args, _, _, _) => { captured = args; return SuccessResult(FourByteFingerprint); });

        await svc.FingerprintAsync(episode, AnalysisMode.Introduction);

        Assert.NotNull(captured);
        Assert.Equal(
            ["-ss", "0", "-i", episode.Path, "-to", "60", "-ac", "2", "-f", "chromaprint", "-fp_format", "raw", "-"],
            captured);
    }

    [Fact]
    public async Task FingerprintAsync_SelectsStdout()
    {
        var episode = CreateEpisode(introEnd: 60);
        FFmpegOutputStream capturedStream = FFmpegOutputStream.Stderr;

        var svc = CreateService(
            runner: (_, stream, _, _) => { capturedStream = stream; return SuccessResult(FourByteFingerprint); });

        await svc.FingerprintAsync(episode, AnalysisMode.Introduction);

        Assert.Equal(FFmpegOutputStream.Stdout, capturedStream);
    }

    [Fact]
    public async Task FingerprintAsync_UsesInfiniteTimeout()
    {
        var episode = CreateEpisode(introEnd: 60);
        TimeSpan? capturedTimeout = TimeSpan.Zero;

        var svc = CreateService(
            runner: (_, _, timeout, _) => { capturedTimeout = timeout; return SuccessResult(FourByteFingerprint); });

        await svc.FingerprintAsync(episode, AnalysisMode.Introduction);

        Assert.Equal(Timeout.InfiniteTimeSpan, capturedTimeout);
    }

    [Fact]
    public async Task FingerprintAsync_CacheHit_ReturnsWithoutRunningFFmpeg()
    {
        var episode = CreateEpisode(introEnd: 60);
        var expected = new uint[] { 1u, 2u, 3u };
        bool runnerCalled = false;

        var cache = new StubCacheService();
        cache.SetCacheHit(
            new DetectionCacheKey(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 60, DetectionCacheVariant.Chromaprint()),
            expected);

        var svc = CreateService(
            runner: (_, _, _, _) => { runnerCalled = true; return SuccessResult(FourByteFingerprint); },
            cache: cache);

        var result = await svc.FingerprintAsync(episode, AnalysisMode.Introduction);

        Assert.False(runnerCalled, "FFmpeg runner must not be called on cache hit");
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task FingerprintAsync_ParsesBinaryUint32Output()
    {
        var episode = CreateEpisode(introEnd: 60);
        var bytes = new byte[12];
        BitConverter.GetBytes(100u).CopyTo(bytes, 0);
        BitConverter.GetBytes(200u).CopyTo(bytes, 4);
        BitConverter.GetBytes(300u).CopyTo(bytes, 8);

        var svc = CreateService(runner: (_, _, _, _) => SuccessResult(bytes));

        var result = await svc.FingerprintAsync(episode, AnalysisMode.Introduction);

        Assert.Equal(new uint[] { 100u, 200u, 300u }, result);
    }

    [Fact]
    public async Task FingerprintAsync_EmptyOutput_ThrowsFingerprintException()
    {
        var episode = CreateEpisode(introEnd: 60);
        var svc = CreateService(runner: (_, _, _, _) => SuccessResult(Array.Empty<byte>()));

        await Assert.ThrowsAsync<FingerprintException>(
            () => svc.FingerprintAsync(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public async Task FingerprintAsync_OddByteCount_ThrowsFingerprintException()
    {
        var episode = CreateEpisode(introEnd: 60);
        var svc = CreateService(runner: (_, _, _, _) => SuccessResult(new byte[] { 1, 2, 3, 4, 5 }));

        await Assert.ThrowsAsync<FingerprintException>(
            () => svc.FingerprintAsync(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public async Task FingerprintAsync_WritesToCacheOnSuccess()
    {
        var episode = CreateEpisode(introEnd: 60);
        var cache = new StubCacheService();

        var svc = CreateService(
            runner: (_, _, _, _) => SuccessResult(FourByteFingerprint),
            cache: cache);

        await svc.FingerprintAsync(episode, AnalysisMode.Introduction);

        Assert.Equal(1, cache.WriteCount);
        Assert.Equal(
            new DetectionCacheKey(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Chromaprint, 0, 60, DetectionCacheVariant.Chromaprint()),
            cache.LastWrittenKey);
        Assert.Equal(new uint[] { 42 }, Assert.IsType<uint[]>(cache.LastWrittenItems));
    }

    [Fact]
    public async Task FingerprintAsync_TimedOut_ThrowsTimeoutException()
    {
        var episode = CreateEpisode(introEnd: 60);
        var svc = CreateService(runner: (_, _, _, _) => TimedOutResult());

        await Assert.ThrowsAsync<TimeoutException>(
            () => svc.FingerprintAsync(episode, AnalysisMode.Introduction));
    }

    [Fact]
    public async Task FingerprintAsync_NonzeroExit_ThrowsFFmpegDetectionException()
    {
        var episode = CreateEpisode(introEnd: 60);
        var cache = new StubCacheService();

        var svc = CreateService(
            runner: (_, _, _, _) => new FFmpegProcessResult(FourByteFingerprint, Array.Empty<byte>(), FFmpegProcessStatus.Completed, 1),
            cache: cache);

        var ex = await Assert.ThrowsAsync<FFmpegDetectionException>(
            () => svc.FingerprintAsync(episode, AnalysisMode.Introduction));

        Assert.Equal(1, ex.ExitCode);
        Assert.Equal(0, cache.WriteCount);
    }

    [Fact]
    public async Task FingerprintAsync_NonzeroExit_UsesDrainedStderrForDiagnostics()
    {
        var episode = CreateEpisode(introEnd: 60);
        const string StderrMessage = "No such file or directory";

        var svc = CreateService(
            runner: (_, _, _, _) => new FFmpegProcessResult(
                FourByteFingerprint,
                Encoding.UTF8.GetBytes(StderrMessage),
                FFmpegProcessStatus.Completed,
                1));

        var ex = await Assert.ThrowsAsync<FFmpegDetectionException>(
            () => svc.FingerprintAsync(episode, AnalysisMode.Introduction));

        Assert.Contains(StderrMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FingerprintAsync_NonzeroExit_LongDiagnosticTruncated()
    {
        var episode = CreateEpisode(introEnd: 60);
        var longMessage = new string('x', 3000);

        var svc = CreateService(
            runner: (_, _, _, _) => new FFmpegProcessResult(
                FourByteFingerprint,
                Encoding.UTF8.GetBytes(longMessage),
                FFmpegProcessStatus.Completed,
                1));

        var ex = await Assert.ThrowsAsync<FFmpegDetectionException>(
            () => svc.FingerprintAsync(episode, AnalysisMode.Introduction));

        Assert.Contains("...", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < longMessage.Length, "Long diagnostic output must be truncated in the exception message");
    }

    // === DetectSilenceAsync: text stderr path ===

    [Fact]
    public async Task DetectSilenceAsync_Arguments_ContainExpectedTokens()
    {
        var episode = CreateEpisode();
        var range = new TimeRange(10, 30);
        IReadOnlyList<string>? captured = null;

        var svc = CreateService(
            runner: (args, _, _, _) => { captured = args; return SuccessResult(""); },
            noise: -45);

        await svc.DetectSilenceAsync(episode, range, AnalysisMode.Introduction);

        Assert.NotNull(captured);
        Assert.Equal(
            ["-vn", "-sn", "-dn", "-ss", "10", "-i", episode.Path, "-to", "20", "-af", "silencedetect=noise=-45dB:duration=0.1", "-f", "null", "-"],
            captured);
    }

    [Fact]
    public async Task DetectSilenceAsync_SelectsStderr()
    {
        var episode = CreateEpisode();
        FFmpegOutputStream capturedStream = FFmpegOutputStream.Stdout;

        var svc = CreateService(
            runner: (_, stream, _, _) => { capturedStream = stream; return SuccessResult(""); });

        await svc.DetectSilenceAsync(episode, new TimeRange(0, 30), AnalysisMode.Introduction);

        Assert.Equal(FFmpegOutputStream.Stderr, capturedStream);
    }

    [Fact]
    public async Task DetectSilenceAsync_UsesInfiniteTimeout()
    {
        var episode = CreateEpisode();
        TimeSpan? capturedTimeout = TimeSpan.Zero;

        var svc = CreateService(
            runner: (_, _, timeout, _) => { capturedTimeout = timeout; return SuccessResult(""); });

        await svc.DetectSilenceAsync(episode, new TimeRange(0, 30), AnalysisMode.Introduction);

        Assert.Equal(Timeout.InfiniteTimeSpan, capturedTimeout);
    }

    [Fact]
    public async Task DetectSilenceAsync_CacheHit_ReturnsWithoutRunningFFmpeg()
    {
        var episode = CreateEpisode();
        var range = new TimeRange(10, 30);
        var expected = new[] { new TimeRange(12, 14) };
        bool runnerCalled = false;

        var cache = new StubCacheService();
        cache.SetCacheHit(
            new DetectionCacheKey(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Silence, 10, 30, DetectionCacheVariant.Silence(-50)),
            expected);

        var svc = CreateService(
            runner: (_, _, _, _) => { runnerCalled = true; return SuccessResult(""); },
            cache: cache);

        var result = await svc.DetectSilenceAsync(episode, range, AnalysisMode.Introduction);

        Assert.False(runnerCalled);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task DetectSilenceAsync_CacheVariant_IncludesNoiseSetting()
    {
        var episode = CreateEpisode();
        var range = new TimeRange(10, 30);
        var expected = new[] { new TimeRange(12, 14) };
        bool runnerCalled = false;

        var cache = new StubCacheService();
        cache.SetCacheHit(
            new DetectionCacheKey(episode.EpisodeId, AnalysisMode.Introduction, CacheEntryType.Silence, 10, 30, DetectionCacheVariant.Silence(-45)),
            expected);

        var svc = CreateService(
            runner: (_, _, _, _) => { runnerCalled = true; return SuccessResult(""); },
            cache: cache,
            noise: -50);

        var result = await svc.DetectSilenceAsync(episode, range, AnalysisMode.Introduction);

        Assert.True(runnerCalled);
        Assert.Empty(result);
    }

    [Fact]
    public async Task DetectSilenceAsync_TimedOut_ThrowsTimeoutException()
    {
        var episode = CreateEpisode();
        var svc = CreateService(runner: (_, _, _, _) => TimedOutResult());

        await Assert.ThrowsAsync<TimeoutException>(
            () => svc.DetectSilenceAsync(episode, new TimeRange(0, 30), AnalysisMode.Introduction));
    }

    [Fact]
    public async Task DetectSilenceAsync_NonzeroExit_IncludesCapturedStderrInExceptionMessage()
    {
        var episode = CreateEpisode();
        var cache = new StubCacheService();
        const string ErrorOutput = "Invalid data found when processing input";

        var svc = CreateService(
            runner: (_, _, _, _) => new FFmpegProcessResult(
                Encoding.UTF8.GetBytes(ErrorOutput), Array.Empty<byte>(), FFmpegProcessStatus.Completed, 1),
            cache: cache);

        var ex = await Assert.ThrowsAsync<FFmpegDetectionException>(
            () => svc.DetectSilenceAsync(episode, new TimeRange(0, 30), AnalysisMode.Introduction));

        Assert.Equal(1, ex.ExitCode);
        Assert.Contains(ErrorOutput, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, cache.WriteCount);
    }

    // === DetectBlackFramesInRangeAsync: text stderr path ===

    [Fact]
    public async Task DetectBlackFramesInRangeAsync_Arguments_ContainExpectedTokens()
    {
        var episode = CreateEpisode();
        var range = new TimeRange(100, 200);
        IReadOnlyList<string>? captured = null;

        var svc = CreateService(
            runner: (args, _, _, _) => { captured = args; return SuccessResult(""); });

        await svc.DetectBlackFramesInRangeAsync(episode, range, 85, 28, AnalysisMode.Credits);

        Assert.NotNull(captured);
        Assert.Equal(
            ["-ss", "100", "-i", episode.Path, "-to", "100", "-an", "-dn", "-sn", "-vf", "blackframe=amount=50:threshold=28", "-f", "null", "-"],
            captured);
    }

    [Fact]
    public async Task DetectBlackFramesInRangeAsync_SelectsStderr()
    {
        var episode = CreateEpisode();
        FFmpegOutputStream capturedStream = FFmpegOutputStream.Stdout;

        var svc = CreateService(
            runner: (_, stream, _, _) => { capturedStream = stream; return SuccessResult(""); });

        await svc.DetectBlackFramesInRangeAsync(episode, new TimeRange(0, 30), 50, 28, AnalysisMode.Credits);

        Assert.Equal(FFmpegOutputStream.Stderr, capturedStream);
    }

    [Fact]
    public async Task DetectBlackFramesInRangeAsync_FiltersMinimumPercentage()
    {
        var episode = CreateEpisode();
        var range = new TimeRange(100, 200);
        var output =
            "[Parsed_blackframe_0 @ 0x0000000] frame:1 pblack:30 pts:43 t:0.043000 type:B last_keyframe:0\n" +
            "[Parsed_blackframe_0 @ 0x0000000] frame:2 pblack:99 pts:85 t:0.085000 type:B last_keyframe:0\n";

        var svc = CreateService(runner: (_, _, _, _) => SuccessResult(output));

        var result = await svc.DetectBlackFramesInRangeAsync(episode, range, 50, 28, AnalysisMode.Credits);

        Assert.Single(result);
        Assert.Equal(99, result[0].Percentage);
    }

    [Fact]
    public async Task DetectBlackFramesInRangeAsync_ReturnsAbsoluteMediaTime()
    {
        var episode = CreateEpisode();
        var range = new TimeRange(100, 200);
        var output =
            "[Parsed_blackframe_0 @ 0x0000000] frame:2 pblack:99 pts:85 t:0.085000 type:B last_keyframe:0\n";

        var svc = CreateService(runner: (_, _, _, _) => SuccessResult(output));

        var result = await svc.DetectBlackFramesInRangeAsync(episode, range, 50, 28, AnalysisMode.Credits);

        var blackFrame = Assert.Single(result);
        Assert.Equal(100.085, blackFrame.Time, 3);
    }

    [Fact]
    public async Task DetectBlackFramesInRangeAsync_CacheVariant_IncludesThreshold()
    {
        var episode = CreateEpisode();
        var range = new TimeRange(100, 200);
        var expected = new[] { new BlackFrame(99, 100.5, 1) };
        bool runnerCalled = false;

        var cache = new StubCacheService();
        cache.SetCacheHit(
            new DetectionCacheKey(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, 100, 200, DetectionCacheVariant.BlackFrameRange(28)),
            expected);

        var svc = CreateService(
            runner: (_, _, _, _) => { runnerCalled = true; return SuccessResult(""); },
            cache: cache);

        var result = await svc.DetectBlackFramesInRangeAsync(episode, range, 50, 32, AnalysisMode.Credits);

        Assert.True(runnerCalled);
        Assert.Empty(result);
    }

    // === DetectCreditBlackFramesAsync ===

    [Fact]
    public async Task DetectCreditBlackFramesAsync_Arguments_ContainKeyframeSkip()
    {
        var episode = CreateEpisode(creditsFingerprintStart: 1500, duration: 1800);
        IReadOnlyList<string>? captured = null;

        var svc = CreateService(
            runner: (args, _, _, _) => { captured = args; return SuccessResult(""); });

        await svc.DetectCreditBlackFramesAsync(episode, 28);

        Assert.NotNull(captured);
        Assert.Equal(
            ["-skip_frame", "nokey", "-ss", "1500", "-i", episode.Path, "-to", "300", "-an", "-dn", "-sn", "-vf", "blackframe=amount=0:threshold=28", "-f", "null", "-"],
            captured);
    }

    [Fact]
    public async Task DetectCreditBlackFramesAsync_UsesCreditsFingerprintRangeInCacheKey()
    {
        var episode = CreateEpisode(creditsFingerprintStart: 1500, duration: 1800);
        var cache = new StubCacheService();
        var expected = new[] { new BlackFrame(99, 0.5, 1) };

        // The credits overload uses the resolved credits fingerprint range in the cache key.
        cache.SetCacheHit(
            new DetectionCacheKey(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, 1500, 1800, DetectionCacheVariant.BlackFrameCredits(28)),
            expected);

        bool runnerCalled = false;
        var svc = CreateService(
            runner: (_, _, _, _) => { runnerCalled = true; return SuccessResult(""); },
            cache: cache);

        var result = await svc.DetectCreditBlackFramesAsync(episode, 28);

        Assert.False(runnerCalled);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task DetectCreditBlackFramesAsync_CacheVariant_IncludesThreshold()
    {
        var episode = CreateEpisode(creditsFingerprintStart: 1500, duration: 1800);
        var cache = new StubCacheService();
        var expected = new[] { new BlackFrame(99, 1500.5, 1) };
        bool runnerCalled = false;

        cache.SetCacheHit(
            new DetectionCacheKey(episode.EpisodeId, AnalysisMode.Credits, CacheEntryType.BlackFrame, 1500, 1800, DetectionCacheVariant.BlackFrameCredits(28)),
            expected);

        var svc = CreateService(
            runner: (_, _, _, _) => { runnerCalled = true; return SuccessResult(""); },
            cache: cache);

        var result = await svc.DetectCreditBlackFramesAsync(episode, 32);

        Assert.True(runnerCalled);
        Assert.Empty(result);
    }

    [Fact]
    public async Task DetectCreditBlackFramesAsync_ReturnsAbsoluteMediaTime()
    {
        var episode = CreateEpisode(creditsFingerprintStart: 1500, duration: 1800);
        var output =
            "[Parsed_blackframe_0 @ 0x0000000] frame:2 pblack:99 pts:85 t:0.085000 type:B last_keyframe:0\n";

        var svc = CreateService(runner: (_, _, _, _) => SuccessResult(output));

        var result = await svc.DetectCreditBlackFramesAsync(episode, 28);

        var blackFrame = Assert.Single(result);
        Assert.Equal(1500.085, blackFrame.Time, 3);
    }

    // === DetectKeyFramesAsync: text stderr path ===

    [Fact]
    public async Task DetectKeyFramesAsync_Arguments_ContainShowinfo()
    {
        var episode = CreateEpisode();
        var range = new TimeRange(50, 100);
        IReadOnlyList<string>? captured = null;

        var svc = CreateService(
            runner: (args, _, _, _) => { captured = args; return SuccessResult(""); });

        await svc.DetectKeyFramesAsync(episode, range, AnalysisMode.Credits);

        Assert.NotNull(captured);
        Assert.Equal(
            ["-skip_frame", "nokey", "-ss", "50", "-i", episode.Path, "-to", "50", "-an", "-dn", "-sn", "-vf", "showinfo", "-f", "null", "-"],
            captured);
    }

    [Fact]
    public async Task DetectKeyFramesAsync_SelectsStderr()
    {
        var episode = CreateEpisode();
        FFmpegOutputStream capturedStream = FFmpegOutputStream.Stdout;

        var svc = CreateService(
            runner: (_, stream, _, _) => { capturedStream = stream; return SuccessResult(""); });

        await svc.DetectKeyFramesAsync(episode, new TimeRange(0, 30), AnalysisMode.Introduction);

        Assert.Equal(FFmpegOutputStream.Stderr, capturedStream);
    }

    [Fact]
    public async Task DetectKeyFramesAsync_UsesInfiniteTimeout()
    {
        var episode = CreateEpisode();
        TimeSpan? capturedTimeout = TimeSpan.Zero;

        var svc = CreateService(
            runner: (_, _, timeout, _) => { capturedTimeout = timeout; return SuccessResult(""); });

        await svc.DetectKeyFramesAsync(episode, new TimeRange(0, 30), AnalysisMode.Introduction);

        Assert.Equal(Timeout.InfiniteTimeSpan, capturedTimeout);
    }

    [Fact]
    public async Task ProbeAudioDurationAsync_RunFailure_ReturnsNull()
    {
        var svc = CreateService(runner: (_, _, _, _) => throw new InvalidOperationException("ffprobe missing"));

        Assert.Null(await svc.ProbeAudioDurationAsync("/media/test.mkv"));
    }

    [Theory]
    [InlineData("123.45\n", 123.45)]
    [InlineData("N/A,00:02:03.5000000\n", 123.5)]
    [InlineData("N/A,00:02:03.500000000\n", 123.5)]
    public void TryParseAudioDuration_ValidValues_ReturnsDuration(string output, double expected)
    {
        Assert.Equal(expected, MediaDetectionService.TryParseAudioDuration(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("N/A\n")]
    [InlineData("0\n")]
    [InlineData("-1\n")]
    public void TryParseAudioDuration_InvalidValues_ReturnsNull(string output)
    {
        Assert.Null(MediaDetectionService.TryParseAudioDuration(output));
    }
    // === Helpers ===

    private static QueuedEpisode CreateEpisode(
        double introEnd = 0,
        double creditsFingerprintStart = 0,
        double duration = 0)
        => new()
        {
            EpisodeId = Guid.NewGuid(),
            Path = "/media/test.mkv",
            IntroFingerprintEnd = introEnd,
            CreditsFingerprintStart = creditsFingerprintStart,
            Duration = duration,
        };

    private static IMediaDetectionService CreateService(
        Func<IReadOnlyList<string>, FFmpegOutputStream, TimeSpan?, CancellationToken, FFmpegProcessResult>? runner = null,
        StubCacheService? cache = null,
        int noise = -50)
    {
        var options = new StubOptions { TestNoise = noise };
        return new MediaDetectionService(
            new DelegatingRunner(runner ?? ((_, _, _, _) => SuccessResult(Array.Empty<byte>()))),
            cache ?? new StubCacheService(),
            options,
            NullLogger<MediaDetectionService>.Instance);
    }

    private static FFmpegProcessResult SuccessResult(byte[] output) => new(output, Array.Empty<byte>(), FFmpegProcessStatus.Completed, 0);

    private static FFmpegProcessResult SuccessResult(string text) => new(Encoding.UTF8.GetBytes(text), Array.Empty<byte>(), FFmpegProcessStatus.Completed, 0);

    private static FFmpegProcessResult TimedOutResult() => new(Array.Empty<byte>(), Array.Empty<byte>(), FFmpegProcessStatus.TimedOut, null);

    private sealed class StubOptions : PluginOptionsProvider
    {
        public int TestNoise { get; set; } = -50;

        public override int SilenceDetectionMaximumNoise => TestNoise;

        public override bool CacheFingerprints => true;

        public override string? FingerprintCachePath => null;
    }

    /// <summary>
    /// Thin delegating runner using Func for behavior injection.
    /// </summary>
    private sealed class DelegatingRunner(
        Func<IReadOnlyList<string>, FFmpegOutputStream, TimeSpan?, CancellationToken, FFmpegProcessResult> handler) : IFFmpegRunner
    {
        public Task<FFmpegProcessResult> RunAsync(
            IReadOnlyList<string> args,
            FFmpegOutputStream outputStream = FFmpegOutputStream.Stdout,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(handler(args, outputStream, timeout, cancellationToken));

        public Task<FFmpegProcessResult> RunFFprobeAsync(
            IReadOnlyList<string> args,
            FFmpegOutputStream outputStream = FFmpegOutputStream.Stdout,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(handler(args, outputStream, timeout, cancellationToken));
    }

    /// <summary>
    /// Thin stub cache using dictionary for cache-hit injection.
    /// </summary>
    private sealed class StubCacheService : IDetectionResultCache
    {
        private readonly Dictionary<DetectionCacheKey, object> _hits = new();

        public int WriteCount { get; private set; }

        public DetectionCacheKey? LastWrittenKey { get; private set; }

        public object? LastWrittenItems { get; private set; }

        public void SetCacheHit<T>(DetectionCacheKey key, T[] data) => _hits[key] = data;

        public Task<T[]?> TryReadJsonCacheAsync<T>(DetectionCacheKey key, CancellationToken cancellationToken = default)
            => Task.FromResult(_hits.TryGetValue(key, out var v) && v is T[] typed ? typed : (T[]?)null);

        public Task<bool> WriteJsonCacheAsync<T>(DetectionCacheKey key, T[] items, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            LastWrittenKey = key;
            LastWrittenItems = items;
            return Task.FromResult(true);
        }

    }
}
