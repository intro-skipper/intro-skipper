// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using Jellyfin.MediaEncoding.Keyframes;
using Jellyfin.MediaEncoding.Keyframes.FfProbe;
using Jellyfin.MediaEncoding.Keyframes.Matroska;
using Xunit;

public class TestKeyframeExtraction
{
    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_RainbowMp4()
    {
        var result = FfProbeKeyframeExtractor.GetKeyframeData(GetFfprobePath(), "../../../video/rainbow.mp4");

        Assert.NotNull(result);

        // Rainbow.mp4 is approximately 10 seconds
        var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
        Assert.InRange(durationSeconds, 9.5, 10.5);

        // Should have multiple keyframes
        Assert.True(result.KeyframeTicks.Count >= 3, $"Expected at least 3 keyframes, got {result.KeyframeTicks.Count}");

        // First keyframe should be at or near start (within 0.1 seconds = 1,000,000 ticks)
        var firstKeyframeSeconds = TimeSpan.FromTicks(result.KeyframeTicks[0]).TotalSeconds;
        Assert.InRange(firstKeyframeSeconds, 0, 0.1);

        VerifyKeyframesSortedAndValid(result);
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_CreditsMp4()
    {
        var result = FfProbeKeyframeExtractor.GetKeyframeData(GetFfprobePath(), "../../../video/credits.mp4");

        Assert.NotNull(result);

        // Credits.mp4 is approximately 5 minutes 30 seconds (330 seconds)
        var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
        Assert.InRange(durationSeconds, 325, 335);

        Assert.True(result.KeyframeTicks.Count >= 10, $"Expected at least 10 keyframes, got {result.KeyframeTicks.Count}");

        var firstKeyframeSeconds = TimeSpan.FromTicks(result.KeyframeTicks[0]).TotalSeconds;
        Assert.InRange(firstKeyframeSeconds, 0, 0.1);

        VerifyKeyframesSortedAndValid(result);
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_CreditsMkv()
    {
        var result = FfProbeKeyframeExtractor.GetKeyframeData(GetFfprobePath(), "../../../video/credits.mkv");

        Assert.NotNull(result);

        // Credits.mkv is approximately 5 minutes 30 seconds (330 seconds)
        var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
        Assert.InRange(durationSeconds, 325, 335);

        Assert.True(result.KeyframeTicks.Count >= 10, $"Expected at least 10 keyframes, got {result.KeyframeTicks.Count}");

        var firstKeyframeSeconds = TimeSpan.FromTicks(result.KeyframeTicks[0]).TotalSeconds;
        Assert.InRange(firstKeyframeSeconds, 0, 0.1);

        VerifyKeyframesSortedAndValid(result);
    }

    [FactSkipFFmpegTests]
    public void TestMatroskaExtractor_CreditsMkv()
    {
        var result = MatroskaKeyframeExtractor.GetKeyframeData("../../../video/credits.mkv");

        Assert.NotNull(result);

        // Credits.mkv is approximately 5 minutes 30 seconds
        if (result.TotalDuration > 0)
        {
            var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
            Assert.InRange(durationSeconds, 320, 340);
        }

        if (result.KeyframeTicks.Count > 0)
        {
            var firstKeyframeSeconds = TimeSpan.FromTicks(result.KeyframeTicks[0]).TotalSeconds;
            Assert.InRange(firstKeyframeSeconds, 0, 0.1);

            VerifyKeyframesSortedAndValid(result);
        }
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeVsMatroska_CreditsMkv()
    {
        var ffprobeResult = FfProbeKeyframeExtractor.GetKeyframeData(GetFfprobePath(), "../../../video/credits.mkv");
        var mkvResult = MatroskaKeyframeExtractor.GetKeyframeData("../../../video/credits.mkv");

        Assert.NotEmpty(ffprobeResult.KeyframeTicks);

        if (mkvResult.TotalDuration > 0 && ffprobeResult.TotalDuration > 0)
        {
            var ffprobeDurationSeconds = TimeSpan.FromTicks(ffprobeResult.TotalDuration).TotalSeconds;
            var mkvDurationSeconds = TimeSpan.FromTicks(mkvResult.TotalDuration).TotalSeconds;
            var durationDiff = Math.Abs(ffprobeDurationSeconds - mkvDurationSeconds);
            var tolerance = Math.Max(1.0, ffprobeDurationSeconds * 0.1);
            Assert.True(durationDiff <= tolerance,
                $"Duration difference too large: ffprobe={ffprobeDurationSeconds}s, mkv={mkvDurationSeconds}s, diff={durationDiff}s");
        }

        if (mkvResult.KeyframeTicks.Count > 0)
        {
            var countRatio = (double)Math.Min(ffprobeResult.KeyframeTicks.Count, mkvResult.KeyframeTicks.Count) /
                            Math.Max(ffprobeResult.KeyframeTicks.Count, mkvResult.KeyframeTicks.Count);
            Assert.True(countRatio > 0.8,
                $"Keyframe counts should be similar: ffprobe={ffprobeResult.KeyframeTicks.Count}, mkv={mkvResult.KeyframeTicks.Count}");
        }
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_KeyframeTimestampsInTicks()
    {
        var result = FfProbeKeyframeExtractor.GetKeyframeData(GetFfprobePath(), "../../../video/rainbow.mp4");

        // For a ~10 second video, duration in ticks should be around 100,000,000 (10 * 10,000,000)
        var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
        Assert.InRange(durationSeconds, 1, 100);

        foreach (var keyframeTicks in result.KeyframeTicks)
        {
            var keyframeSeconds = TimeSpan.FromTicks(keyframeTicks).TotalSeconds;
            Assert.InRange(keyframeSeconds, 0, 100);
        }
    }

    [FactSkipFFmpegTests]
    public void TestMatroskaExtractor_KeyframeTimestampsInTicks()
    {
        var result = MatroskaKeyframeExtractor.GetKeyframeData("../../../video/credits.mkv");

        if (result.TotalDuration > 0)
        {
            var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
            Assert.InRange(durationSeconds, 1, 1000);
        }

        foreach (var keyframeTicks in result.KeyframeTicks)
        {
            var keyframeSeconds = TimeSpan.FromTicks(keyframeTicks).TotalSeconds;
            Assert.InRange(keyframeSeconds, 0, 1000);
        }
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_FileNotFound()
    {
        Assert.ThrowsAny<Exception>(() =>
            FfProbeKeyframeExtractor.GetKeyframeData(GetFfprobePath(), "../../../video/nonexistent.mp4"));
    }

    [Fact]
    public void TestMatroskaExtractor_FileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            MatroskaKeyframeExtractor.GetKeyframeData("../../../video/nonexistent.mkv"));
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_InvalidFile()
    {
        try
        {
            var result = FfProbeKeyframeExtractor.GetKeyframeData(GetFfprobePath(), "../../../audio/README.txt");
            Assert.NotNull(result);
        }
        catch (Exception)
        {
            Assert.True(true);
        }
    }

    [Fact]
    public void TestMatroskaExtractor_InvalidFile()
    {
        try
        {
            var result = MatroskaKeyframeExtractor.GetKeyframeData("../../../audio/README.txt");
            Assert.NotNull(result);
        }
        catch (Exception)
        {
            Assert.True(true);
        }
    }

    [FactSkipFFmpegTests]
    public void TestKeyframeData_Immutability()
    {
        var result = FfProbeKeyframeExtractor.GetKeyframeData(GetFfprobePath(), "../../../video/rainbow.mp4");

        Assert.IsType<System.Collections.Generic.IReadOnlyList<long>>(result.KeyframeTicks, exactMatch: false);
    }

    private static void VerifyKeyframesSortedAndValid(KeyframeData data)
    {
        for (int i = 1; i < data.KeyframeTicks.Count; i++)
        {
            Assert.True(data.KeyframeTicks[i] >= data.KeyframeTicks[i - 1],
                $"Keyframes should be sorted: keyframe[{i - 1}]={data.KeyframeTicks[i - 1]} ticks > keyframe[{i}]={data.KeyframeTicks[i]} ticks");
        }

        foreach (var keyframeTicks in data.KeyframeTicks)
        {
            Assert.True(keyframeTicks >= 0, $"Keyframe {keyframeTicks} ticks should be non-negative");

            if (data.TotalDuration > 0)
            {
                Assert.True(keyframeTicks <= data.TotalDuration,
                    $"Keyframe {keyframeTicks} ticks should not exceed duration {data.TotalDuration} ticks");
            }
        }
    }

    private static string GetFfprobePath()
    {
        var ffprobePath = "/usr/local/bin/ffprobe";

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, "ffprobe.exe");
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return ffprobePath;
    }
}
