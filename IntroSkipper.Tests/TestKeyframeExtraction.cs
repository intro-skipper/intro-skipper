// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using IntroSkipper.Analyzers;
using IntroSkipper.Data;
using Jellyfin.MediaEncoding.Keyframes;
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
        var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
        Assert.InRange(durationSeconds, 9.5, 10.5);

        // Should have multiple keyframes
        Assert.True(result.KeyframeTicks.Count >= 3, $"Expected at least 3 keyframes, got {result.KeyframeTicks.Count}");

        // First keyframe should be at or near start (within 0.1 seconds = 1,000,000 ticks)
        var firstKeyframeSeconds = TimeSpan.FromTicks(result.KeyframeTicks[0]).TotalSeconds;
        Assert.InRange(firstKeyframeSeconds, 0, 0.1);

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
        var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
        Assert.InRange(durationSeconds, 325, 335);

        // Should have many keyframes for a 5+ minute video
        Assert.True(result.KeyframeTicks.Count >= 10, $"Expected at least 10 keyframes, got {result.KeyframeTicks.Count}");

        // First keyframe should be at or near start (within 0.1 seconds)
        var firstKeyframeSeconds = TimeSpan.FromTicks(result.KeyframeTicks[0]).TotalSeconds;
        Assert.InRange(firstKeyframeSeconds, 0, 0.1);

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
        var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
        Assert.InRange(durationSeconds, 325, 335);

        // Should have many keyframes
        Assert.True(result.KeyframeTicks.Count >= 10, $"Expected at least 10 keyframes, got {result.KeyframeTicks.Count}");

        // First keyframe should be at or near start (within 0.1 seconds)
        var firstKeyframeSeconds = TimeSpan.FromTicks(result.KeyframeTicks[0]).TotalSeconds;
        Assert.InRange(firstKeyframeSeconds, 0, 0.1);

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
        if (result.TotalDuration > 0)
        {
            var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
            Assert.InRange(durationSeconds, 320, 340);
        }

        // If keyframes exist, verify they are valid
        if (result.KeyframeTicks.Count > 0)
        {
            // First keyframe should be at or near start (within 0.1 seconds)
            var firstKeyframeSeconds = TimeSpan.FromTicks(result.KeyframeTicks[0]).TotalSeconds;
            Assert.InRange(firstKeyframeSeconds, 0, 0.1);

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
        Assert.NotEmpty(ffprobeResult.KeyframeTicks);

        // Duration comparison - if both extracted duration
        if (mkvResult.TotalDuration > 0 && ffprobeResult.TotalDuration > 0)
        {
            var ffprobeDurationSeconds = TimeSpan.FromTicks(ffprobeResult.TotalDuration).TotalSeconds;
            var mkvDurationSeconds = TimeSpan.FromTicks(mkvResult.TotalDuration).TotalSeconds;
            var durationDiff = Math.Abs(ffprobeDurationSeconds - mkvDurationSeconds);
            var tolerance = Math.Max(1.0, ffprobeDurationSeconds * 0.1);
            Assert.True(durationDiff <= tolerance,
                $"Duration difference too large: ffprobe={ffprobeDurationSeconds}s, mkv={mkvDurationSeconds}s, diff={durationDiff}s");
        }

        // Keyframe count comparison - if MKV extractor found keyframes
        if (mkvResult.KeyframeTicks.Count > 0)
        {
            // Counts might differ due to parsing differences, but should be similar (within 20%)
            var countRatio = (double)Math.Min(ffprobeResult.KeyframeTicks.Count, mkvResult.KeyframeTicks.Count) /
                            Math.Max(ffprobeResult.KeyframeTicks.Count, mkvResult.KeyframeTicks.Count);
            Assert.True(countRatio > 0.8,
                $"Keyframe counts should be similar: ffprobe={ffprobeResult.KeyframeTicks.Count}, mkv={mkvResult.KeyframeTicks.Count}");
        }
    }

    [FactSkipFFmpegTests]
    public void TestFfprobeExtractor_KeyframeTimestampsInTicks()
    {
        var extractor = CreateFfprobeExtractor();
        var episode = QueueFile("rainbow.mp4");

        var result = extractor.GetKeyframeData(episode.Path);

        // Verify timestamps are in ticks (not seconds or milliseconds)
        // For a ~10 second video, duration in ticks should be around 100,000,000 (10 * 10,000,000)
        var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
        Assert.InRange(durationSeconds, 1, 100);

        // Verify each keyframe is in ticks and within reasonable range
        foreach (var keyframeTicks in result.KeyframeTicks)
        {
            var keyframeSeconds = TimeSpan.FromTicks(keyframeTicks).TotalSeconds;
            Assert.InRange(keyframeSeconds, 0, 100);
        }
    }

    [FactSkipFFmpegTests]
    public void TestMkvExtractor_KeyframeTimestampsInTicks()
    {
        var extractor = new MkvKeyframeExtractor();
        var episode = QueueFile("credits.mkv");

        var result = extractor.GetKeyframeData(episode.Path);

        // Verify timestamps are in ticks (not seconds or milliseconds)
        // For a ~330 second video, duration in ticks should be around 3,300,000,000
        if (result.TotalDuration > 0)
        {
            var durationSeconds = TimeSpan.FromTicks(result.TotalDuration).TotalSeconds;
            Assert.InRange(durationSeconds, 1, 1000);
        }

        // Verify each keyframe is in ticks and within reasonable range
        foreach (var keyframeTicks in result.KeyframeTicks)
        {
            var keyframeSeconds = TimeSpan.FromTicks(keyframeTicks).TotalSeconds;
            Assert.InRange(keyframeSeconds, 0, 1000);
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

        // Verify that KeyframeTicks is read-only (IReadOnlyList<long>)
        Assert.IsType<System.Collections.Generic.IReadOnlyList<long>>(result.KeyframeTicks, exactMatch: false);
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
        for (int i = 1; i < data.KeyframeTicks.Count; i++)
        {
            Assert.True(data.KeyframeTicks[i] >= data.KeyframeTicks[i - 1],
                $"Keyframes should be sorted: keyframe[{i - 1}]={data.KeyframeTicks[i - 1]} ticks > keyframe[{i}]={data.KeyframeTicks[i]} ticks");
        }

        // Verify all keyframes are non-negative and within duration
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
