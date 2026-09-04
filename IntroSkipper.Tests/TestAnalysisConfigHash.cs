// SPDX-FileCopyrightText: 2026 rlauuzo
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
}
