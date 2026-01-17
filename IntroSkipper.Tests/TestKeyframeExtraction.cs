// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;
using Xunit;

public class TestKeyframeExtraction
{
    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_RainbowMp4()
    {
        var extractor = CreateFfprobeExtractor();
        var episode = QueueFile("rainbow.mp4");

        var result = extractor.GetKeyframeData(episode.Path);

        Assert.NotNull(result);

        // Rainbow.mp4 is approximately 10 seconds
        Assert.InRange(result.Duration, 9.5, 10.5);

        // Should have multiple keyframes
        Assert.True(result.Keyframes.Count >= 3, $"Expected at least 3 keyframes, got {result.Keyframes.Count}");

        // First keyframe should be at or near start
        Assert.InRange(result.Keyframes[0], 0, 0.1);

        // Verify keyframes are sorted and within bounds
        VerifyKeyframesSortedAndValid(result);
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_CreditsMp4()
    {
        var extractor = CreateFfprobeExtractor();
        var episode = QueueFile("credits.mp4");

        var result = extractor.GetKeyframeData(episode.Path);

        Assert.NotNull(result);

        // Credits.mp4 is approximately 5 minutes 30 seconds (330 seconds)
        Assert.InRange(result.Duration, 325, 335);

        // Should have many keyframes for a 5+ minute video
        Assert.True(result.Keyframes.Count >= 10, $"Expected at least 10 keyframes, got {result.Keyframes.Count}");

        // First keyframe should be at or near start
        Assert.InRange(result.Keyframes[0], 0, 0.1);

        // Verify keyframes are sorted and within bounds
        VerifyKeyframesSortedAndValid(result);
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_CreditsMkv()
    {
        var extractor = CreateFfprobeExtractor();
        var episode = QueueFile("credits.mkv");

        var result = extractor.GetKeyframeData(episode.Path);

        Assert.NotNull(result);

        // Credits.mkv is approximately 5 minutes 30 seconds (330 seconds)
        Assert.InRange(result.Duration, 325, 335);

        // Should have many keyframes
        Assert.True(result.Keyframes.Count >= 10, $"Expected at least 10 keyframes, got {result.Keyframes.Count}");

        // First keyframe should be at or near start
        Assert.InRange(result.Keyframes[0], 0, 0.1);

        // Verify keyframes are sorted and within bounds
        VerifyKeyframesSortedAndValid(result);
    }

    [FactSkipFFmpegTests]
    public void TestMkvExtractor_CreditsMkv()
    {
        var extractor = new MkvKeyframeExtractor();
        var episode = QueueFile("credits.mkv");

        var result = extractor.GetKeyframeData(episode.Path);

        Assert.NotNull(result);

        // MKV parser should extract duration (may vary slightly from ffprobe)
        // Credits.mkv is approximately 5 minutes 30 seconds
        if (result.Duration > 0)
        {
            Assert.InRange(result.Duration, 320, 340);
        }

        // If keyframes exist, verify they are valid
        if (result.Keyframes.Count > 0)
        {
            // First keyframe should be at or near start
            Assert.InRange(result.Keyframes[0], 0, 0.1);

            // Verify keyframes are sorted and within bounds
            VerifyKeyframesSortedAndValid(result);
        }
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeVsMkv_CreditsMkv()
    {
        var ffprobeExtractor = CreateFfprobeExtractor();
        var mkvExtractor = new MkvKeyframeExtractor();
        var episode = QueueFile("credits.mkv");

        var ffprobeResult = ffprobeExtractor.GetKeyframeData(episode.Path);
        var mkvResult = mkvExtractor.GetKeyframeData(episode.Path);

        // Ffprobe should always find keyframes
        Assert.NotEmpty(ffprobeResult.Keyframes);

        // Duration comparison - if both extracted duration
        if (mkvResult.Duration > 0 && ffprobeResult.Duration > 0)
        {
            var durationDiff = Math.Abs(ffprobeResult.Duration - mkvResult.Duration);
            var tolerance = Math.Max(1.0, ffprobeResult.Duration * 0.1);
            Assert.True(durationDiff <= tolerance,
                $"Duration difference too large: ffprobe={ffprobeResult.Duration}s, mkv={mkvResult.Duration}s, diff={durationDiff}s");
        }

        // Keyframe count comparison - if MKV extractor found keyframes
        if (mkvResult.Keyframes.Count > 0)
        {
            // Counts might differ due to parsing differences, but should be similar (within 20%)
            var countRatio = (double)Math.Min(ffprobeResult.Keyframes.Count, mkvResult.Keyframes.Count) /
                            Math.Max(ffprobeResult.Keyframes.Count, mkvResult.Keyframes.Count);
            Assert.True(countRatio > 0.8,
                $"Keyframe counts should be similar: ffprobe={ffprobeResult.Keyframes.Count}, mkv={mkvResult.Keyframes.Count}");
        }
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_KeyframeTimestampsInSeconds()
    {
        var extractor = CreateFfprobeExtractor();
        var episode = QueueFile("rainbow.mp4");

        var result = extractor.GetKeyframeData(episode.Path);

        // Verify timestamps are in seconds (not milliseconds or other units)
        // For a ~10 second video, duration should be around 10, not 10000
        Assert.InRange(result.Duration, 1, 100);

        foreach (var keyframe in result.Keyframes)
        {
            Assert.InRange(keyframe, 0, 100);
        }
    }

    [FactSkipFFmpegTests]
    public void TestMkvExtractor_KeyframeTimestampsInSeconds()
    {
        var extractor = new MkvKeyframeExtractor();
        var episode = QueueFile("credits.mkv");

        var result = extractor.GetKeyframeData(episode.Path);

        // Verify timestamps are in seconds
        // For a ~330 second video, duration should be around 330, not 330000
        if (result.Duration > 0)
        {
            Assert.InRange(result.Duration, 1, 1000);
        }

        foreach (var keyframe in result.Keyframes)
        {
            Assert.InRange(keyframe, 0, 1000);
        }
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_FileNotFound()
    {
        var extractor = CreateFfprobeExtractor();
        var episode = QueueFile("nonexistent.mp4");

        Assert.Throws<FileNotFoundException>(() => extractor.GetKeyframeData(episode.Path));
    }

    [Fact]
    public void TestMkvExtractor_FileNotFound()
    {
        var extractor = new MkvKeyframeExtractor();
        var episode = QueueFile("nonexistent.mkv");

        Assert.Throws<FileNotFoundException>(() => extractor.GetKeyframeData(episode.Path));
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_InvalidFile()
    {
        var extractor = CreateFfprobeExtractor();
        var textFile = "../../../audio/README.txt";

        // Text file might not throw if ffprobe can read it
        // Just verify it doesn't crash
        try
        {
            var result = extractor.GetKeyframeData(textFile);
            Assert.NotNull(result);
        }
        catch (InvalidOperationException)
        {
            // This is expected for invalid files
            Assert.True(true);
        }
    }

    [Fact]
    public void TestMkvExtractor_InvalidFile()
    {
        var extractor = new MkvKeyframeExtractor();
        var textFile = "../../../audio/README.txt";

        // Text file might not throw if it doesn't have valid EBML structure
        // Just verify it doesn't crash
        try
        {
            var result = extractor.GetKeyframeData(textFile);
            Assert.NotNull(result);
        }
        catch (InvalidDataException)
        {
            // This is expected for invalid files
            Assert.True(true);
        }
    }

    [FactSkipFFmpegTests]
    public void TestKeyframeData_Immutability()
    {
        var extractor = CreateFfprobeExtractor();
        var episode = QueueFile("rainbow.mp4");

        var result = extractor.GetKeyframeData(episode.Path);

        // Verify that Keyframes is read-only
        Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<double>>(result.Keyframes);
    }

    private static QueuedEpisode QueueFile(string path)
    {
        return new()
        {
            EpisodeId = Guid.NewGuid(),
            Name = path,
            Path = "../../../video/" + path
        };
    }

    private static void VerifyKeyframesSortedAndValid(KeyframeData data)
    {
        // Verify keyframes are sorted
        for (int i = 1; i < data.Keyframes.Count; i++)
        {
            Assert.True(data.Keyframes[i] >= data.Keyframes[i - 1],
                $"Keyframes should be sorted: keyframe[{i - 1}]={data.Keyframes[i - 1]} > keyframe[{i}]={data.Keyframes[i]}");
        }

        // Verify all keyframes are non-negative and within duration
        foreach (var keyframe in data.Keyframes)
        {
            Assert.True(keyframe >= 0, $"Keyframe {keyframe} should be non-negative");

            if (data.Duration > 0)
            {
                Assert.True(keyframe <= data.Duration,
                    $"Keyframe {keyframe} should not exceed duration {data.Duration}");
            }
        }
    }

    private static FfprobeKeyframeExtractor CreateFfprobeExtractor()
    {
        var logger = new LoggerFactory().CreateLogger<FfprobeKeyframeExtractor>();
        // WindowsFfmpegTestBootstrap adds ffprobe to PATH, so we can just use "ffprobe"
        // But we need the full path for the extractor's File.Exists check
        var ffprobePath = "ffprobe";

        // Try to find ffprobe.exe in PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var path in pathEnv.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(path, "ffprobe.exe");
                if (File.Exists(fullPath))
                {
                    ffprobePath = fullPath;
                    break;
                }
            }
        }

        return new FfprobeKeyframeExtractor(ffprobePath, logger);
    }
}
