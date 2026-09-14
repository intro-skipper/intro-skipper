// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Xunit;

namespace IntroSkipper.Tests;

public sealed class TestAnalysisConfigHash
{
    public static TheoryData<AnalysisMode, PluginConfiguration, PluginConfiguration, AnalyzerAction> InvalidatingChanges => new()
    {
        // MinimumIntroDuration bounds the Chromaprint credits region, so it belongs to the credits hash.
        { AnalysisMode.Credits, new PluginConfiguration { MinimumIntroDuration = 15 }, new PluginConfiguration { MinimumIntroDuration = 30 }, AnalyzerAction.Default },
        { AnalysisMode.Preview, new PluginConfiguration { AnimePreviewFromCreditsEnd = false }, new PluginConfiguration { AnimePreviewFromCreditsEnd = true }, AnalyzerAction.Default },
        { AnalysisMode.Recap, new PluginConfiguration(), new PluginConfiguration { MaximumFingerprintPointDifferences = new PluginConfiguration().MaximumFingerprintPointDifferences + 1 }, AnalyzerAction.Default },
        { AnalysisMode.Recap, new PluginConfiguration(), new PluginConfiguration { AnchorRecapToColdOpen = true }, AnalyzerAction.Default },
        { AnalysisMode.Introduction, new PluginConfiguration(), new PluginConfiguration(), AnalyzerAction.Chapter },
    };

    [Theory]
    [MemberData(nameof(InvalidatingChanges))]
    public void Analysis_ChangesWhenRelevantInputChanges(AnalysisMode mode, PluginConfiguration before, PluginConfiguration after, AnalyzerAction afterAction)
    {
        Assert.NotEqual(
            ConfigHasher.Analysis(before, mode, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(after, mode, afterAction, ffmpegValid: true));
    }

    [Fact]
    public void ExplainAnalysisHashChange_IdentifiesAnalyzerActionChange()
    {
        var config = new PluginConfiguration();
        var storedHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Chapter, ffmpegValid: true);

        Assert.Equal(
            "analyzer action changed from Chapter to Default",
            ConfigHasher.ExplainAnalysisHashChange(config, AnalysisMode.Introduction, AnalyzerAction.Default, true, storedHash));
    }

    [Fact]
    public void ExplainAnalysisHashChange_IdentifiesChromaprintAvailabilityChange()
    {
        var config = new PluginConfiguration();
        var storedHash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: false);

        Assert.Equal(
            "Chromaprint availability changed from false to true",
            ConfigHasher.ExplainAnalysisHashChange(config, AnalysisMode.Introduction, AnalyzerAction.Default, true, storedHash));
    }

    [Fact]
    public void ExplainAnalysisHashChange_UsesHonestFallbackForUnknownSettingChange()
    {
        var config = new PluginConfiguration();
        var storedHash = ConfigHasher.Analysis(new PluginConfiguration { AnalysisPercent = 10 }, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true);

        Assert.Equal(
            "one or more analysis settings or analyzer-version inputs changed (previous input values are not stored)",
            ConfigHasher.ExplainAnalysisHashChange(config, AnalysisMode.Introduction, AnalyzerAction.Default, true, storedHash));
    }
}
