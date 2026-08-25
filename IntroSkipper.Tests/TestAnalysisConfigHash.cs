// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// The per-season config hash is the only thing that retires stale automatic segments, so every
/// setting an analyzer reads has to change the hash of the mode it affects. These tests cover the
/// settings that are named for one mode but change the result of another, which is where that
/// property is easiest to break.
/// </summary>
public sealed class TestAnalysisConfigHash
{
    private static string Hash(PluginConfiguration config, AnalysisMode mode)
        => ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, ffmpegValid: true);

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    public void MinimumIntroDuration_ChangesHash_ForEveryModeChromaprintAppliesItTo(AnalysisMode mode)
    {
        // ChromaprintAnalyzer.GetMinimumRegionDuration feeds MinimumIntroDuration to every mode except
        // Recap, so raising it has to invalidate credits as well as introductions.
        var before = new PluginConfiguration { MinimumIntroDuration = 15 };
        var after = new PluginConfiguration { MinimumIntroDuration = 30 };

        Assert.NotEqual(Hash(before, mode), Hash(after, mode));
    }

    [Fact]
    public void MinimumIntroDuration_DoesNotChangeRecapHash()
    {
        // Recap uses a fixed card duration instead, so folding the setting in would retire recaps for
        // a change that cannot affect them.
        var before = new PluginConfiguration { MinimumIntroDuration = 15 };
        var after = new PluginConfiguration { MinimumIntroDuration = 30 };

        Assert.Equal(Hash(before, AnalysisMode.Recap), Hash(after, AnalysisMode.Recap));
    }

    [Theory]
    [InlineData(AnalysisMode.Credits)]
    [InlineData(AnalysisMode.Preview)]
    public void AnimePreviewFromCreditsEnd_ChangesHash_ForBothModesItProduces(AnalysisMode mode)
    {
        // The setting is read while analyzing Credits but writes a Preview segment, so turning it off
        // has to invalidate the previews it created.
        var before = new PluginConfiguration { AnimePreviewFromCreditsEnd = true };
        var after = new PluginConfiguration { AnimePreviewFromCreditsEnd = false };

        Assert.NotEqual(Hash(before, mode), Hash(after, mode));
    }

    [Fact]
    public void AnalyzerAction_ChangesHash()
    {
        var config = new PluginConfiguration();

        Assert.NotEqual(
            ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Chapter, ffmpegValid: true));
    }
}
