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
        { AnalysisMode.Recap, new PluginConfiguration { EnableSubtitleRecapDetection = true }, new PluginConfiguration { EnableSubtitleRecapDetection = true, SubtitleRecapPattern = "previously" }, AnalyzerAction.Default },
        { AnalysisMode.Preview, new PluginConfiguration(), new PluginConfiguration { EnableSubtitlePreviewDetection = true }, AnalyzerAction.Default },
        { AnalysisMode.Preview, new PluginConfiguration { EnableSubtitlePreviewDetection = true }, new PluginConfiguration { EnableSubtitlePreviewDetection = true, SubtitlePreviewPattern = "preview" }, AnalyzerAction.Default },
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

    [Theory]
    [InlineData(AnalysisMode.Recap, true)]
    [InlineData(AnalysisMode.Preview, false)]
    public void SubtitleDisabled_UsesConventionalHashAndRecognizesPriorSubtitleHash(AnalysisMode mode, bool recap)
    {
        var config = new PluginConfiguration();
        var conventionalHash = ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, true, includeSubtitleSettings: false);
        var defaultHash = ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, true);
        var changedDisabledPattern = recap
            ? new PluginConfiguration { SubtitleRecapPattern = "recap marker" }
            : new PluginConfiguration { SubtitlePreviewPattern = "preview marker" };
        var previouslyEnabled = recap
            ? new PluginConfiguration { EnableSubtitleRecapDetection = true }
            : new PluginConfiguration { EnableSubtitlePreviewDetection = true };
        var previousHash = ConfigHasher.Analysis(previouslyEnabled, mode, AnalyzerAction.Default, true);

        Assert.Equal(conventionalHash, defaultHash);
        Assert.Equal(defaultHash, ConfigHasher.Analysis(changedDisabledPattern, mode, AnalyzerAction.Default, true));
        Assert.True(ConfigHasher.IsSubtitleOnlyHashChange(conventionalHash, conventionalHash));
        Assert.True(ConfigHasher.IsSubtitleOnlyHashChange(previousHash, conventionalHash));
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
            EnableSubtitleRecapDetection = true,
            EnableSubtitlePreviewDetection = true,
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
