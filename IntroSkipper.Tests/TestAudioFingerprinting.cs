// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

/* These tests require that the host system has a version of FFmpeg installed
 * which supports both chromaprint and the "-fp_format raw" flag.
 */

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntroSkipper.Tests;

public class TestAudioFingerprinting
{
    [FactSkipFFmpegTests]
    public async Task TestInstallationCheck()
    {
        Assert.True(await CreateCapabilityService().CheckFFmpegVersionAsync().ConfigureAwait(true));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 213)]
    [InlineData(10, 56_021)]
    [InlineData(16, 16_112_341)]
    [InlineData(19, 2_465_585_877)]
    public void TestBitCounting(int expectedBits, uint number)
    {
        Assert.Equal(expectedBits, BitOperations.PopCount(number));
    }

    [FactSkipFFmpegTests]
    public async Task TestFingerprinting()
    {
        // Generated with `fpcalc -raw audio/big_buck_bunny_intro.mp3`
        var expected = new uint[]{
            3269995649, 3261610160, 3257403872, 1109989680, 1109993760, 1110010656, 1110142768, 1110175504,
            1110109952, 1126874880, 2788611, 2787586, 6981634, 15304754, 28891170, 43579426, 43542561,
            47737888, 41608640, 40559296, 36352644, 53117572, 2851460, 1076465548, 1080662428, 1080662492,
            1089182044, 1148041501, 1148037422, 3291343918, 3290980398, 3429367854, 3437756714, 3433698090,
            3433706282, 3366600490, 3366464314, 2296916250, 3362269210, 3362265115, 3362266441, 3370784472,
            3366605480, 1218990776, 1223217816, 1231602328, 1260950200, 1245491640, 169845176, 1510908120,
            1510911000, 2114365528, 2114370008, 1996929688, 1996921480, 1897171592, 1884588680, 1347470984,
            1343427226, 1345467054, 1349657318, 1348673570, 1356869666, 1356865570, 295837698, 60957698,
            44194818, 48416770, 40011778, 36944210, 303147954, 369146786, 1463847842, 1434488738, 1417709474,
            1417713570, 3699441634, 3712167202, 3741460534, 2585144342, 2597725238, 2596200487, 2595926077,
            2595984141, 2594734600, 2594736648, 2598931176, 2586348264, 2586348264, 2586561257, 2586451659,
            2603225802, 2603225930, 2573860970, 2561151018, 3634901034, 3634896954, 3651674122, 3416793162,
            3416816715, 3404331257, 3395844345, 3395836155, 3408464089, 3374975369, 1282036360, 1290457736,
            1290400440, 1290314408, 1281925800, 1277727404, 1277792932, 1278785460, 1561962388, 1426698196,
            3607924711, 4131892839, 4140215815, 4292259591, 3218515717, 3209938229, 3171964197, 3171956013,
            4229117295, 4229312879, 4242407935, 4240114111, 4239987133, 4239990013, 3703060732, 1547188252,
            1278748677, 1278748935, 1144662786, 1148854786, 1090388802, 1090388962, 1086260130, 1085940098,
            1102709122, 45811586, 44634002, 44596656, 44592544, 1122527648, 1109944736, 1109977504, 1111030243,
            1111017762, 1109969186, 1126721826, 1101556002, 1084844322, 1084979506, 1084914450, 1084914449,
            1084873520, 3228093296, 3224996817, 3225062275, 3241840002, 3346701698, 3349843394, 3349782306,
            3349719842, 3353914146, 3328748322, 3328747810, 3328809266, 3471476754, 3472530451, 3472473123,
            3472417825, 3395841056, 3458735136, 3341420624, 1076496560, 1076501168, 1076501136, 1076497024
        };

        var actual = await CreateDetectionService().FingerprintAsync(
            QueueEpisode("audio/big_buck_bunny_intro.mp3"),
            AnalysisMode.Introduction);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TestIndexGeneration()
    {
        //                     0  1  2  3  4  5   6   7
        var fpr = new uint[] { 1, 2, 3, 1, 5, 77, 42, 2 };
        var expected = new Dictionary<uint, int>()
        {
            {1, 3},
            {2, 7},
            {3, 2},
            {5, 4},
            {42, 6},
            {77, 5},
        };

        var analyzer = CreateChromaprintAnalyzer();
        var actual = analyzer.CreateInvertedIndex(Guid.NewGuid(), fpr);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AnalyzeMediaFiles_ContinuesWhenFingerprintingTimesOut()
    {
        WarningManager.Clear();

        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());
        var analyzer = CreateChromaprintAnalyzer(new FailingMediaDetectionService(
            fingerprintException: new TimeoutException("FFmpeg process timed out before completing media detection.")));
        var episodes = new[]
        {
            QueueEpisode("episode1.mkv"),
            QueueEpisode("episode2.mkv"),
        };

        var result = await analyzer.AnalyzeMediaFiles(episodes, AnalysisMode.Introduction, CancellationToken.None);

        Assert.Same(episodes, result);
        Assert.Contains(nameof(PluginWarning.InvalidChromaprintFingerprint), WarningManager.GetWarnings(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeMediaFiles_ChecksCachedFingerprintsSequentially()
    {
        using var pluginScope = new EntrypointTestHelpers.PluginInstanceScope(EntrypointTestHelpers.CreateTempCacheDir());
        EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration());
        var cacheService = new CountingCacheService();
        var analyzer = CreateChromaprintAnalyzer(
            new FailingMediaDetectionService(fingerprintException: new NotSupportedException()),
            cacheService);
        var episodes = new[]
        {
            QueueEpisode("episode1.mkv"),
            QueueEpisode("episode2.mkv"),
            QueueEpisode("episode3.mkv"),
            QueueEpisode("episode4.mkv"),
        };
        foreach (var episode in episodes)
        {
            episode.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.Analyzed);
        }

        await analyzer.AnalyzeMediaFiles(episodes, AnalysisMode.Introduction, CancellationToken.None);

        Assert.Equal(episodes.Length, cacheService.CallCount);
        Assert.Equal(1, cacheService.MaxConcurrent);
    }

    [FactSkipFFmpegTests]
    public async Task TestIntroDetection()
    {
        var chromaprint = CreateChromaprintAnalyzer();

        var lhsEpisode = QueueEpisode("audio/big_buck_bunny_intro.mp3");
        var rhsEpisode = QueueEpisode("audio/big_buck_bunny_clip.mp3");
        var detectionService = CreateDetectionService();
        var lhsFingerprint = await detectionService.FingerprintAsync(lhsEpisode, AnalysisMode.Introduction);
        var rhsFingerprint = await detectionService.FingerprintAsync(rhsEpisode, AnalysisMode.Introduction);

        var (lhs, rhs) = chromaprint.CompareEpisodes(
            lhsEpisode.EpisodeId,
            lhsFingerprint,
            rhsEpisode.EpisodeId,
            rhsFingerprint);

        Assert.True(lhs.Valid);
        Assert.Equal(0, lhs.Start);
        Assert.Equal(17.214, lhs.End, 3);

        Assert.True(rhs.Valid);
        // because we changed for 0.128 to 0.1238 its 4,952 now but that's too early (<= 5)
        Assert.Equal(0, rhs.Start);
        Assert.Equal(22.1673, rhs.End, 3);
    }

    /// <summary>
    /// Test that the silencedetect wrapper is working.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task TestSilenceDetection()
    {
        var clip = QueueEpisode("audio/big_buck_bunny_clip.mp3");

        var expected = new TimeRange[]
        {
            new(44.631042, 44.807167),
            new(53.590521, 53.806979),
            new(53.845833, 54.202417),
            new(54.261104, 54.593479),
            new(54.709792, 54.929312),
            new(54.929396, 55.258979),
        };

        var range = new TimeRange(0, 60);
        var actual = await CreateDetectionService().DetectSilenceAsync(clip, range, AnalysisMode.Introduction);

        Assert.Equal(expected, actual);
    }

    private static QueuedEpisode QueueEpisode(string path)
    {
        return new QueuedEpisode()
        {
            EpisodeId = Guid.NewGuid(),
            Path = "../../../" + path,
            IntroFingerprintEnd = 60
        };
    }

    private static FFmpegCapabilityService CreateCapabilityService() => TestServiceFactory.CreateCapabilityService();

    private static IMediaDetectionService CreateDetectionService() => TestServiceFactory.CreateDetectionService();

    private static ChromaprintAnalyzer CreateChromaprintAnalyzer()
        => CreateChromaprintAnalyzer(TestServiceFactory.CreateDetectionService());

    private static ChromaprintAnalyzer CreateChromaprintAnalyzer(IMediaDetectionService detectionService)
        => CreateChromaprintAnalyzer(detectionService, TestServiceFactory.CreateCacheService());

    private static ChromaprintAnalyzer CreateChromaprintAnalyzer(
        IMediaDetectionService detectionService,
        IDetectionCacheService cacheService)
    {
        var logger = new LoggerFactory().CreateLogger<ChromaprintAnalyzer>();
        return new(logger, detectionService, cacheService);
    }

    private sealed class CountingCacheService : IDetectionCacheService
    {
        private readonly object _syncLock = new();
        private int _current;

        public int CallCount { get; private set; }

        public int MaxConcurrent { get; private set; }

        public async Task<bool> HasCachedFingerprintAsync(QueuedEpisode episode, AnalysisMode mode, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _current);
            lock (_syncLock)
            {
                CallCount++;
                MaxConcurrent = Math.Max(MaxConcurrent, current);
            }

            try
            {
                await Task.Delay(25, cancellationToken);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        public Task<T[]?> TryReadJsonCacheAsync<T>(DetectionCacheKey key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> WriteJsonCacheAsync<T>(DetectionCacheKey key, T[] items, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteFingerprintCacheAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteCacheFilesAsync(AnalysisMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteStaleCachesAsync(IReadOnlySet<Guid> enabledItemIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MigrateLegacyCachesAsync(IEnumerable<QueuedEpisode> episodes, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

}

public sealed class FactSkipFFmpegTests : FactAttribute
{
#if SKIP_FFMPEG_TESTS
    public FactSkipFFmpegTests() {
        Skip = "SKIP_FFMPEG_TESTS defined, skipping unit tests that require FFmpeg to be installed";
    }
#endif
}
