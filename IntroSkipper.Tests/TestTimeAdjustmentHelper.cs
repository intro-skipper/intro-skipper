// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestTimeAdjustmentHelper
{
    private static (TimeAdjustmentHelper helper, PluginConfiguration cfg) CreateHelper(PluginConfiguration? cfg = null)
    {
        cfg ??= new PluginConfiguration
        {
            EndSnapThreshold = 2.0,
            AdjustIntroBasedOnChapters = false,
            AdjustIntroBasedOnSilence = false,
            SnapToKeyframe = false,
            AdjustWindowInward = 2.0,
            AdjustWindowOutward = 2.0,
            IntroStartOffset = 0,
            IntroEndOffset = 0,
        };

        return (new TimeAdjustmentHelper(new NullLoggerFactory().CreateLogger("Test"), cfg), cfg);
    }

    [Fact]
    public void StartOffset_IsIgnored_When_SnappingToEpisodeStart()
    {
        var (helper, cfg) = CreateHelper();
        cfg.IntroStartOffset = 2; // user-configured offset

        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 60 };
        var original = new Segment(episode.EpisodeId) { Start = 1.2, End = 10 };

        var adjusted = helper.AdjustIntroTimes(episode, original);

        Assert.Equal(0, adjusted.Start);
        Assert.Equal(10, adjusted.End);
    }

    [Fact]
    public void StartOffset_IsApplied_When_NotSnapping()
    {
        var (helper, cfg) = CreateHelper();
        cfg.IntroStartOffset = 2;

        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 60 };
        var original = new Segment(episode.EpisodeId) { Start = 5, End = 12 };

        var adjusted = helper.AdjustIntroTimes(episode, original);

        Assert.Equal(7, adjusted.Start);
        Assert.Equal(12, adjusted.End);
    }

    [Fact]
    public void Start_And_End_Are_Clamped_To_Duration()
    {
        var (helper, cfg) = CreateHelper();
        cfg.IntroStartOffset = 0;
        cfg.IntroEndOffset = 100; // will try to push end negative

        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), Duration = 30 };
        var original = new Segment(episode.EpisodeId) { Start = -5, End = 200 };

        var adjusted = helper.AdjustIntroTimes(episode, original);

        Assert.Equal(0, adjusted.Start); // clamped from -5 to 0 and snapped
        Assert.Equal(30, adjusted.End);  // clamped from 200 to duration before end logic kicks in
    }
}
