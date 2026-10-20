// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

/* These tests require that the host system has a version of FFmpeg installed
 * which supports both chromaprint and the "-fp_format raw" flag.
 */

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestAudioFingerprinting
{
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

        var ffmpegService = CreateFFmpegService();
        var actual = await ffmpegService.FingerprintAsync(
            QueueEpisode("audio/big_buck_bunny_intro.mp3"),
            AnalysisMode.Introduction);

        Assert.Equal(expected, actual);
    }

    [FactSkipFFmpegTests]
    public async Task TestIntroDetection()
    {
        var chromaprint = CreateChromaprintAnalyzer();

        var lhsEpisode = QueueEpisode("audio/big_buck_bunny_intro.mp3");
        var rhsEpisode = QueueEpisode("audio/big_buck_bunny_clip.mp3");
        var ffmpegService = CreateFFmpegService();
        var lhsFingerprint = await ffmpegService.FingerprintAsync(lhsEpisode, AnalysisMode.Introduction);
        var rhsFingerprint = await ffmpegService.FingerprintAsync(rhsEpisode, AnalysisMode.Introduction);

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
    /// ffmpeg's own audio pick ranks the default-disposition stream above one with more channels,
    /// so a preferred language on the 5.1 stream must map it and must not reuse the cached row of ffmpeg's pick.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task PreferredLanguage_FingerprintsNonDefaultStreamWithMoreChannels()
    {
        using var directory = new TempDirectory();
        var dualTrack = directory.Join("dual.mkv");
        var surroundOnly = directory.Join("surround.mkv");
        var runner = new FFmpegProcessRunner(NullLogger.Instance);
        await runner.RunAsync(
            "ffmpeg",
            [
                "-y", "-v", "error",
                "-i", "../../../audio/big_buck_bunny_intro.mp3",
                "-i", "../../../audio/big_buck_bunny_clip.mp3",
                "-map", "0:a", "-map", "1:a",
                "-c:a", "flac", "-ac:a:0", "2", "-ac:a:1", "6",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=jpn",
                "-disposition:a:0", "default", "-disposition:a:1", "0",
                dualTrack,
            ]);
        await runner.RunAsync("ffmpeg", ["-y", "-v", "error", "-i", dualTrack, "-map", "0:a:1", "-c", "copy", surroundOnly]);

        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var ffmpegService = CreateFFmpegService();
        var surround = await ffmpegService.FingerprintAsync(QueueAbsolute(surroundOnly), AnalysisMode.Introduction);
        var episode = QueueAbsolute(dualTrack);
        var ffmpegPick = await ffmpegService.FingerprintAsync(episode, AnalysisMode.Introduction);

        Plugin.Instance!.Configuration.PreferredAudioLanguage = "jpn";
        var preferred = await ffmpegService.FingerprintAsync(episode, AnalysisMode.Introduction);

        Assert.NotEqual(surround, ffmpegPick);
        Assert.Equal(surround, preferred);

        static QueuedEpisode QueueAbsolute(string path) => new() { EpisodeId = Guid.NewGuid(), Path = path, IntroFingerprintEnd = 60 };
    }

    /// <summary>
    /// 12.0 labeled its own pick, most channels then lowest index, with the default variant. Where that is
    /// not ffmpeg's pick, a preferred language on ffmpeg's pick must not reuse such a row.
    /// </summary>
    [FactSkipFFmpegTests]
    public async Task PreferredLanguage_IgnoresDefaultVariantRowWhenFfmpegPicksTheDefaultStream()
    {
        using var directory = new TempDirectory();
        var dualTrack = directory.Join("dual.mkv");
        var surroundOnly = directory.Join("surround.mkv");
        var stereoOnly = directory.Join("stereo.mkv");
        var runner = new FFmpegProcessRunner(NullLogger.Instance);
        await runner.RunAsync(
            "ffmpeg",
            [
                "-y", "-v", "error",
                "-i", "../../../audio/big_buck_bunny_clip.mp3",
                "-i", "../../../audio/big_buck_bunny_intro.mp3",
                "-map", "0:a", "-map", "1:a",
                "-c:a", "flac", "-ac:a:0", "6", "-ac:a:1", "2",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=jpn",
                "-disposition:a:0", "0", "-disposition:a:1", "default",
                dualTrack,
            ]);
        await runner.RunAsync("ffmpeg", ["-y", "-v", "error", "-i", dualTrack, "-map", "0:a:0", "-c", "copy", surroundOnly]);
        await runner.RunAsync("ffmpeg", ["-y", "-v", "error", "-i", dualTrack, "-map", "0:a:1", "-c", "copy", stereoOnly]);

        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration());
        var ffmpegService = CreateFFmpegService();
        var episodeId = Guid.NewGuid();
        var stereo = await ffmpegService.FingerprintAsync(Queue(Guid.NewGuid(), stereoOnly), AnalysisMode.Introduction);

        // The row 12.0 wrote for the dual-track episode with PreferAudioStreamWithMostChannels off: the 5.1 stream under the default variant.
        var surround = await ffmpegService.FingerprintAsync(Queue(episodeId, surroundOnly), AnalysisMode.Introduction);

        Plugin.Instance!.Configuration.PreferredAudioLanguage = "jpn";
        var preferred = await ffmpegService.FingerprintAsync(Queue(episodeId, dualTrack), AnalysisMode.Introduction);

        Assert.NotEqual(surround, stereo);
        Assert.Equal(stereo, preferred);

        static QueuedEpisode Queue(Guid id, string path) => new() { EpisodeId = id, Path = path, IntroFingerprintEnd = 60 };
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
        var actual = await CreateFFmpegService().DetectSilenceAsync(clip, range, AnalysisMode.Introduction);

        // ffmpeg's silencedetect timestamps vary slightly between versions (e.g. 6.1.1
        // reports 44.631 where 7.x reports 44.631042), so compare with a tolerance.
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Start, actual[i].Start, 0.01);
            Assert.Equal(expected[i].End, actual[i].End, 0.01);
        }
    }

    private static QueuedEpisode QueueEpisode(string path)
    {
        var episode = FfmpegTestHelpers.QueueFile(path);
        episode.IntroFingerprintEnd = 60;
        return episode;
    }

    private static FFmpegService CreateFFmpegService() => FfmpegTestHelpers.CreateFFmpegService();

    /// <summary>
    /// A file whose keyframe snap throws on every pass, here a codec probe cut before its stream
    /// mapping, fails only its own episode; its sibling still gets the shared intro.
    /// </summary>
    [Fact]
    public async Task FailedKeyframeSnap_FailsOnlyItsEpisode()
    {
        var seasonId = Guid.NewGuid();
        QueuedEpisode[] episodes =
        [
            new() { EpisodeId = Guid.NewGuid(), SeasonId = seasonId, EpisodeNumber = 1, Duration = 1800 },
            new() { EpisodeId = Guid.NewGuid(), SeasonId = seasonId, EpisodeNumber = 2, Duration = 1800 },
        ];
        var ffmpeg = new StubFFmpegService
        {
            // Points 200 to 450, about 31 s, are shared; every other point differs in eight bits.
            Fingerprints = (episode, _) =>
            [
                .. Enumerable.Range(0, 3600).Select(i => i is >= 200 and <= 450
                    ? 0x40000000u + ((uint)i * 7919u)
                    : (uint)i ^ (0x11111111u * (uint)episode.EpisodeNumber)),
            ],
            KeyFrames = (episode, _, _) => episode == episodes[0] ? throw new InvalidDataException("cut before the stream mapping") : [],
        };
        var database = DatabaseTestHelpers.CreateTempSegmentDatabase();
        var config = new PluginConfiguration { AdjustIntroBasedOnSilence = false, SnapToKeyframe = true };
        var analyzer = new ChromaprintAnalyzer(NullLogger<ChromaprintAnalyzer>.Instance, ffmpeg, null!, database, config);

        await analyzer.AnalyzeMediaFiles(episodes, AnalysisMode.Introduction, CancellationToken.None);

        Assert.Equal(EpisodeState.AnalysisFailed, episodes[0].GetAnalyzed(AnalysisMode.Introduction));
        Assert.Equal(EpisodeState.Analyzed, episodes[1].GetAnalyzed(AnalysisMode.Introduction));
        Assert.Single(await database.GetSegmentsAsync(episodes[1].EpisodeId));
    }

    private static ChromaprintAnalyzer CreateChromaprintAnalyzer()
    {
        return new(NullLogger<ChromaprintAnalyzer>.Instance, null!, null!, DatabaseTestHelpers.CreateTempSegmentDatabase());
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
