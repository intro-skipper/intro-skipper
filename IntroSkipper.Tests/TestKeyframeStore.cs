// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using Jellyfin.MediaEncoding.Keyframes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The keyframe lookup behind the intro-end snap: served from Jellyfin's keyframe store, and
/// extracted with Jellyfin's own extractors and stored there when the store has no row.
/// </summary>
public sealed class TestKeyframeStore
{
    // seek-sample.mp4 has one keyframe per second.
    private static readonly double[] SeekSampleKeyframes = [0, 1, 2, 3, 4, 5, 6, 7];

    [FactSkipFFmpegTests]
    public async Task ColdStore_Mp4_ExtractsWithFfprobeAndStores()
    {
        var store = new FakeKeyframeManager();
        var episode = FfmpegTestHelpers.QueueFile("video/seek-sample.mp4");

        var actual = await FfmpegTestHelpers.CreateFFmpegService(keyframeManager: store).GetKeyframesAsync(episode, new(0, 8));

        Assert.Equal(SeekSampleKeyframes, actual);
        var stored = Assert.Single(store.GetKeyframeData(episode.EpisodeId));
        Assert.Equal(SeekSampleKeyframes, stored.KeyframeTicks.Select(TickConversions.ToSeconds));
        Assert.Equal(1, store.SaveCount);
    }

    [FactSkipFFmpegTests]
    public async Task ColdStore_Mkv_ExtractsFromCues()
    {
        var store = new FakeKeyframeManager();
        var mkvPath = Path.Combine(Path.GetTempPath(), $"intro-skipper-{Guid.NewGuid():N}.mkv");
        try
        {
            var source = FfmpegTestHelpers.QueueFile("video/seek-sample.mp4").Path;
            await new FFmpegProcessRunner(NullLogger.Instance).RunAsync("ffmpeg", ["-y", "-i", source, "-c", "copy", mkvPath]);
            var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Name = "seek-sample.mkv", Path = mkvPath };

            var actual = await FfmpegTestHelpers.CreateFFmpegService(keyframeManager: store).GetKeyframesAsync(episode, new(0, 8));

            Assert.Equal(SeekSampleKeyframes, actual);
            Assert.Equal(1, store.SaveCount);
        }
        finally
        {
            File.Delete(mkvPath);
        }
    }

    [Fact]
    public async Task WarmStore_ServesStoredRowWithoutExtracting()
    {
        var store = new FakeKeyframeManager();
        var episode = FfmpegTestHelpers.QueueFile("video/seek-sample.mp4");
        store.Seed(episode.EpisodeId, new KeyframeData(TickConversions.FromSeconds(10), [TickConversions.FromSeconds(1.5), TickConversions.FromSeconds(3), TickConversions.FromSeconds(9)]));

        var actual = await FfmpegTestHelpers.CreateFFmpegService(keyframeManager: store).GetKeyframesAsync(episode, new(0, 8));

        Assert.Equal([1.5, 3], actual);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task FailedExtraction_ReturnsEmptyAndStoresNothing()
    {
        var store = new FakeKeyframeManager();
        var episode = FfmpegTestHelpers.QueueFile("video/missing.mp4");

        var actual = await FfmpegTestHelpers.CreateFFmpegService(keyframeManager: store).GetKeyframesAsync(episode, new(0, 8));

        Assert.Empty(actual);
        Assert.Equal(0, store.SaveCount);
    }
}
