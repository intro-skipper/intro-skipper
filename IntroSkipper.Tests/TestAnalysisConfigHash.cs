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

        var recapConventionalHash = ConfigHasher.Analysis(config, AnalysisMode.Recap, AnalyzerAction.Default, true, includeSubtitleSettings: false);
        var previewConventionalHash = ConfigHasher.Analysis(config, AnalysisMode.Preview, AnalyzerAction.Default, true, includeSubtitleSettings: false);
        Assert.Equal(recapConventionalHash, recapDefault);
        Assert.Equal(previewConventionalHash, previewDefault);

        var changedDisabledPatterns = new PluginConfiguration
        {
            SubtitleRecapPattern = "recap marker",
            SubtitlePreviewPattern = "preview marker",
        };
        Assert.Equal(recapDefault, ConfigHasher.Analysis(changedDisabledPatterns, AnalysisMode.Recap, AnalyzerAction.Default, true));
        Assert.Equal(previewDefault, ConfigHasher.Analysis(changedDisabledPatterns, AnalysisMode.Preview, AnalyzerAction.Default, true));

        var enabledRecapHash = ConfigHasher.Analysis(
            new PluginConfiguration(),
            AnalysisMode.Recap,
            AnalyzerAction.Default,
            true,
            subtitleRecapDetectionOverride: true);
        var enabledPreviewHash = ConfigHasher.Analysis(
            new PluginConfiguration(),
            AnalysisMode.Preview,
            AnalyzerAction.Default,
            true,
            subtitlePreviewDetectionOverride: true);
        Assert.NotEqual(
            enabledRecapHash,
            ConfigHasher.Analysis(
                changedDisabledPatterns,
                AnalysisMode.Recap,
                AnalyzerAction.Default,
                true,
                subtitleRecapDetectionOverride: true));
        Assert.NotEqual(
            enabledPreviewHash,
            ConfigHasher.Analysis(
                changedDisabledPatterns,
                AnalysisMode.Preview,
                AnalyzerAction.Default,
                true,
                subtitlePreviewDetectionOverride: true));
        Assert.True(ConfigHasher.IsSubtitleOnlyHashChange(enabledRecapHash, recapDefault));
        Assert.True(ConfigHasher.IsSubtitleOnlyHashChange(enabledPreviewHash, previewDefault));
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    [InlineData(AnalysisMode.Commercial)]
    public void SubtitleDetectionSettings_DoNotInvalidateUnrelatedModesOrChromaprintCache(AnalysisMode mode)
    {
        var before = new PluginConfiguration();
        var after = new PluginConfiguration
        {
            SubtitleRecapPattern = "recap",
            SubtitlePreviewPattern = "preview",
        };

        Assert.Equal(
            ConfigHasher.Analysis(before, mode, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(after, mode, AnalyzerAction.Default, ffmpegValid: true));
        Assert.Equal(
            ConfigHasher.DetectionCache(before, CacheEntryType.Chromaprint, AnalysisMode.Introduction),
            ConfigHasher.DetectionCache(after, CacheEntryType.Chromaprint, AnalysisMode.Introduction));
        Assert.Equal(
            ConfigHasher.DetectionCache(before, CacheEntryType.Chromaprint, AnalysisMode.Recap),
            ConfigHasher.DetectionCache(after, CacheEntryType.Chromaprint, AnalysisMode.Recap));
    }
}
