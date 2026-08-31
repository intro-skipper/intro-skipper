// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.ScheduledTasks;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestAnalysisConfigHash
{
    [Fact]
    public void MinimumIntroDuration_InvalidatesCreditsHash()
    {
        var before = new PluginConfiguration { MinimumIntroDuration = 15 };
        var after = new PluginConfiguration { MinimumIntroDuration = 30 };

        Assert.NotEqual(
            ConfigHasher.Analysis(before, AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(after, AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: true));
    }

    [Fact]
    public void AnimePreviewSetting_InvalidatesPreviewHash()
    {
        var before = new PluginConfiguration { AnimePreviewFromCreditsEnd = false };
        var after = new PluginConfiguration { AnimePreviewFromCreditsEnd = true };

        Assert.NotEqual(
            ConfigHasher.Analysis(before, AnalysisMode.Preview, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(after, AnalysisMode.Preview, AnalyzerAction.Default, ffmpegValid: true));
    }

    [Fact]
    public void AnalyzerAction_InvalidatesHash()
    {
        var config = new PluginConfiguration();

        Assert.NotEqual(
            ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Chapter, ffmpegValid: true));
    }

    [Fact]
    public void FailedItemsAreNotPersistedAsAnalyzed()
    {
        var failed = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        failed.SetAnalyzed(AnalysisMode.Credits, EpisodeState.AnalysisFailed);
        var completed = new QueuedEpisode { EpisodeId = Guid.NewGuid() };
        completed.SetAnalyzed(AnalysisMode.Credits, EpisodeState.NoSegments);

        var ids = BaseItemAnalyzerTask.GetPersistableEpisodeIds([failed, completed], AnalysisMode.Credits);

        Assert.Equal([completed.EpisodeId], ids);
        Assert.True(failed.NeedsAnalysis(AnalysisMode.Credits));
    }
}
