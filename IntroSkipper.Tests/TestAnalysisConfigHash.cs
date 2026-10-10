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
        { AnalysisMode.Recap, new PluginConfiguration(), new PluginConfiguration { EnableSubtitleRecapDetection = true }, AnalyzerAction.Default },
        { AnalysisMode.Recap, new PluginConfiguration(), new PluginConfiguration { SubtitleRecapPattern = "previously" }, AnalyzerAction.Default },
        { AnalysisMode.Preview, new PluginConfiguration(), new PluginConfiguration { EnableSubtitlePreviewDetection = true }, AnalyzerAction.Default },
        { AnalysisMode.Preview, new PluginConfiguration(), new PluginConfiguration { SubtitlePreviewPattern = "preview" }, AnalyzerAction.Default },
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
    public void Analysis_SeasonSubtitleOverridesOnlyAffectTheirOwnMode()
    {
        var config = new PluginConfiguration();
        var recapDefault = ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.Default, true);
        var previewDefault = ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, true);

        Assert.NotEqual(
            recapDefault,
            ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.Default, true, subtitleRecapDetectionOverride: true));
        Assert.NotEqual(
            previewDefault,
            ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, true, subtitlePreviewDetectionOverride: true));
        Assert.Equal(
            recapDefault,
            ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.Default, true, subtitlePreviewDetectionOverride: true));
        Assert.Equal(
            previewDefault,
            ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, true, subtitleRecapDetectionOverride: true));
    }
}
