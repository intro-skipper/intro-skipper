// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Linq;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using Xunit;

/// <summary>
/// Which settings invalidate which hash: a cache or analysis hash must change for
/// every setting that changes the produced data and stay put for every setting that
/// does not, or the library is either re-analyzed needlessly or served stale results.
/// </summary>
public sealed class TestConfigHasher
{
    private const string MostChannelsStreamCacheVariant = "policy=most-channels";

    public static TheoryData<string, string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, string, bool>();

        void Case(string name, string first, string second, bool expectEqual) => data.Add(name, first, second, expectEqual);
        static string Cache(PluginConfiguration config, CacheEntryType type, AnalysisMode mode, string? variant = null)
            => ConfigHasher.DetectionCache(config, type, mode, variant);
        static string Analysis(PluginConfiguration config, AnalysisMode mode, bool ffmpegValid = true)
            => ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, ffmpegValid);

        var threshold32 = new PluginConfiguration { BlackFrameThreshold = 32 };
        var threshold64 = new PluginConfiguration { BlackFrameThreshold = 64 };
        var percentage85 = new PluginConfiguration { BlackFrameThreshold = 32, BlackFrameMinimumPercentage = 85 };
        var percentage95 = new PluginConfiguration { BlackFrameThreshold = 32, BlackFrameMinimumPercentage = 95 };
        var defaults = new PluginConfiguration();
        var english = new PluginConfiguration { PreferredAudioLanguage = "eng" };
        var englishPadded = new PluginConfiguration { PreferredAudioLanguage = " ENG " };
        var mostChannels = new PluginConfiguration { PreferAudioStreamWithMostChannels = true };
        var lowestIndex = new PluginConfiguration { PreferAudioStreamWithMostChannels = false };
        var englishMostChannels = new PluginConfiguration { PreferredAudioLanguage = "eng", PreferAudioStreamWithMostChannels = true };
        var nonBlackOn = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = false, DetectNonBlackCredits = true };
        var nonBlackOff = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = false, DetectNonBlackCredits = false };
        var legacyNonBlackOn = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, DetectNonBlackCredits = true };
        var legacyNonBlackOff = new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, DetectNonBlackCredits = false };

        Case("BlackFrame cache changes with threshold", Cache(threshold32, CacheEntryType.BlackFrame, AnalysisMode.Credits), Cache(threshold64, CacheEntryType.BlackFrame, AnalysisMode.Credits), false);
        Case("BlackFrame cache changes with mode", Cache(threshold32, CacheEntryType.BlackFrame, AnalysisMode.Introduction), Cache(threshold32, CacheEntryType.BlackFrame, AnalysisMode.Credits), false);
        Case("BlackFrame cache ignores minimum percentage", Cache(percentage85, CacheEntryType.BlackFrame, AnalysisMode.Credits), Cache(percentage95, CacheEntryType.BlackFrame, AnalysisMode.Credits), true);
        Case("BlackInterval cache changes with threshold", Cache(threshold32, CacheEntryType.BlackInterval, AnalysisMode.Credits), Cache(threshold64, CacheEntryType.BlackInterval, AnalysisMode.Credits), false);
        Case("BlackInterval cache changes with mode", Cache(threshold32, CacheEntryType.BlackInterval, AnalysisMode.Introduction), Cache(threshold32, CacheEntryType.BlackInterval, AnalysisMode.Credits), false);
        Case("BlackInterval cache differs from BlackFrame", Cache(threshold32, CacheEntryType.BlackFrame, AnalysisMode.Credits), Cache(threshold32, CacheEntryType.BlackInterval, AnalysisMode.Credits), false);

        // BlackFrameMinimumPercentage is passed to blackdetect as pic_th and is baked into the
        // cached intervals, so changing it must invalidate the BlackInterval detection cache.
        Case("BlackInterval cache varies with minimum percentage", Cache(percentage85, CacheEntryType.BlackInterval, AnalysisMode.Credits), Cache(percentage95, CacheEntryType.BlackInterval, AnalysisMode.Credits), false);

        Case("Chromaprint cache ignores preferred language without an effective stream", Cache(defaults, CacheEntryType.Chromaprint, AnalysisMode.Introduction), Cache(english, CacheEntryType.Chromaprint, AnalysisMode.Introduction), true);
        Case("Chromaprint cache ignores processing settings", Cache(defaults, CacheEntryType.Chromaprint, AnalysisMode.Introduction), Cache(new PluginConfiguration { MaximumFingerprintPointDifferences = defaults.MaximumFingerprintPointDifferences + 1 }, CacheEntryType.Chromaprint, AnalysisMode.Introduction), true);
        Case("Chromaprint cache normalizes preferred language", Cache(english, CacheEntryType.Chromaprint, AnalysisMode.Introduction), Cache(englishPadded, CacheEntryType.Chromaprint, AnalysisMode.Introduction), true);
        Case("Chromaprint cache changes with effective stream", Cache(mostChannels, CacheEntryType.Chromaprint, AnalysisMode.Introduction, "policy=most-channels"), Cache(lowestIndex, CacheEntryType.Chromaprint, AnalysisMode.Introduction, "stream-index=0"), false);
        Case("Chromaprint stream identity reuses across selection settings", Cache(englishMostChannels, CacheEntryType.Chromaprint, AnalysisMode.Introduction, MostChannelsStreamCacheVariant), Cache(lowestIndex, CacheEntryType.Chromaprint, AnalysisMode.Introduction, MostChannelsStreamCacheVariant), true);

        Case("Analysis changes with stream selection policy", Analysis(mostChannels, AnalysisMode.Introduction), Analysis(lowestIndex, AnalysisMode.Introduction), false);
        Case("Introduction analysis changes with preferred language", Analysis(defaults, AnalysisMode.Introduction), Analysis(english, AnalysisMode.Introduction), false);
        Case("Credits analysis changes with preferred language", Analysis(defaults, AnalysisMode.Credits), Analysis(english, AnalysisMode.Credits), false);
        Case("Recap analysis changes with preferred language", Analysis(defaults, AnalysisMode.Recap), Analysis(english, AnalysisMode.Recap), false);

        // Toggling card credits changes credits output (when its analyzer is active), so it
        // must invalidate stored credits analysis instead of hash-matching a stale result. The legacy
        // BlackFrameAnalyzer cannot observe DetectNonBlackCredits, so toggling it must not invalidate
        // stored credits analysis on that path.
        Case("Credits analysis changes with DetectNonBlackCredits", Analysis(nonBlackOn, AnalysisMode.Credits), Analysis(nonBlackOff, AnalysisMode.Credits), false);
        Case("Credits analysis changes when legacy analyzer selected", Analysis(nonBlackOff, AnalysisMode.Credits), Analysis(legacyNonBlackOff, AnalysisMode.Credits), false);
        Case("Credits analysis ignores DetectNonBlackCredits under legacy analyzer", Analysis(legacyNonBlackOn, AnalysisMode.Credits), Analysis(legacyNonBlackOff, AnalysisMode.Credits), true);

        // Chromaprint availability changes what the Chromaprint-backed modes can produce;
        // the chapter-only modes never consult it.
        Case("Introduction analysis changes with chromaprint availability", Analysis(defaults, AnalysisMode.Introduction), Analysis(defaults, AnalysisMode.Introduction, ffmpegValid: false), false);
        Case("Credits analysis changes with chromaprint availability", Analysis(defaults, AnalysisMode.Credits), Analysis(defaults, AnalysisMode.Credits, ffmpegValid: false), false);
        Case("Credits analysis changes with a BlackFrame action", Analysis(defaults, AnalysisMode.Credits), ConfigHasher.Analysis(defaults, AnalysisMode.Credits, AnalyzerAction.BlackFrame, ffmpegValid: true), false);
        // The credits pass never consults chapters under a BlackFrame action, so the enhancement
        // option cannot change those seasons' results and must not re-scan them.
        Case("Credits analysis ignores chapter enhancement under a BlackFrame action", ConfigHasher.Analysis(defaults, AnalysisMode.Credits, AnalyzerAction.BlackFrame, ffmpegValid: true), ConfigHasher.Analysis(new PluginConfiguration { EnhanceChapterCredits = true }, AnalysisMode.Credits, AnalyzerAction.BlackFrame, ffmpegValid: true), true);
        // The string is frozen to what releases before the credits pass wrote, so seasons the
        // first-wins chain settled stay settled after the upgrade instead of re-scanning.
        Case("Credits analysis hash is pinned", Analysis(defaults, AnalysisMode.Credits), "353008E48A5F559E", true);
        Case("Credits analysis ignores the preview minimum; the Preview hash carries it", Analysis(defaults, AnalysisMode.Credits), Analysis(new PluginConfiguration { MinimumPreviewDuration = 30 }, AnalysisMode.Credits), true);
        Case("Preview analysis changes with the preview minimum", Analysis(defaults, AnalysisMode.Preview), Analysis(new PluginConfiguration { MinimumPreviewDuration = 30 }, AnalysisMode.Preview), false);
        Case("Recap analysis changes with chromaprint availability", Analysis(defaults, AnalysisMode.Recap), Analysis(defaults, AnalysisMode.Recap, ffmpegValid: false), false);
        Case("Preview analysis ignores chromaprint availability", Analysis(defaults, AnalysisMode.Preview), Analysis(defaults, AnalysisMode.Preview, ffmpegValid: false), true);
        Case("Commercial analysis ignores chromaprint availability", Analysis(defaults, AnalysisMode.Commercial), Analysis(defaults, AnalysisMode.Commercial, ffmpegValid: false), true);

        return data;
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    [InlineData(AnalysisMode.Recap)]
    [InlineData(AnalysisMode.Preview)]
    [InlineData(AnalysisMode.Commercial)]
    public void ChapterEnhancement_OnlyInvalidatesCreditsAnalysis(AnalysisMode mode)
    {
        var defaults = new PluginConfiguration();
        var enhanced = new PluginConfiguration { EnhanceChapterCredits = true };
        var baseline = ConfigHasher.Analysis(defaults, mode, AnalyzerAction.Default, true);
        var changed = ConfigHasher.Analysis(enhanced, mode, AnalyzerAction.Default, true);

        Assert.Equal(mode != AnalysisMode.Credits, baseline == changed);
        foreach (var type in Enum.GetValues<CacheEntryType>())
        {
            Assert.Equal(ConfigHasher.DetectionCache(defaults, type, mode), ConfigHasher.DetectionCache(enhanced, type, mode));
        }
    }

    [Fact]
    public void LegacyChromaprintCacheHash_MatchesPreStreamSelectionRows()
    {
        // Pinned output for a default configuration; rows written by releases without audio
        // stream selection carry exactly this value. Changing it drops every episode those
        // releases analyzed out of the Chromaprint comparison pool.
        var hash = ConfigHasher.LegacyChromaprintCacheWithoutLanguage(new PluginConfiguration(), AnalysisMode.Introduction);

        Assert.Equal("1CD6171D4F6FA587", hash);

        // The legacy hash predates audio stream selection, so those settings must not affect it.
        var changedSelection = new PluginConfiguration { PreferredAudioLanguage = "eng", PreferAudioStreamWithMostChannels = false };
        Assert.Equal(hash, ConfigHasher.LegacyChromaprintCacheWithoutLanguage(changedSelection, AnalysisMode.Introduction));
    }

    /// <summary>
    /// A BlackFrame action restricts the credits pass to the keyframe analyzer, which cannot observe
    /// chapter enhancement — but only while that method is enabled. With it off the action falls back
    /// to the default policy, which does consult chapters, so the option must invalidate again or the
    /// season keeps credits produced under the other setting.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ChapterEnhancementUnderABlackFrameAction_IsIgnoredOnlyWhileTheKeyframeScanRuns(bool keyframeEnabled, bool expectEqual)
    {
        var plain = new PluginConfiguration { EnableKeyframeAnalyzer = keyframeEnabled };
        var enhanced = new PluginConfiguration { EnableKeyframeAnalyzer = keyframeEnabled, EnhanceChapterCredits = true };

        var first = ConfigHasher.Analysis(plain, AnalysisMode.Credits, AnalyzerAction.BlackFrame, ffmpegValid: true);
        var second = ConfigHasher.Analysis(enhanced, AnalysisMode.Credits, AnalyzerAction.BlackFrame, ffmpegValid: true);

        Assert.Equal(expectEqual, first == second);
    }

    /// <summary>
    /// A conditional token drops out once no analyzer can read its setting, so toggling that setting
    /// does not re-scan seasons whose result it cannot change. Card credits and the recap cold-open
    /// anchor both need the keyframe scan; chapter enhancement needs chapter analysis.
    /// </summary>
    [Theory]
    [InlineData(AnalysisMode.Credits, "card-credits")]
    [InlineData(AnalysisMode.Recap, "cold-open")]
    [InlineData(AnalysisMode.Credits, "chapter-enhancement")]
    public void SettingsNoEnabledMethodReads_DoNotInvalidateAnalysis(AnalysisMode mode, string setting)
    {
        static PluginConfiguration Configure(string setting, bool enabled) => setting switch
        {
            "card-credits" => new PluginConfiguration { EnableKeyframeAnalyzer = false, DetectNonBlackCredits = enabled },
            "cold-open" => new PluginConfiguration { EnableKeyframeAnalyzer = false, AnchorRecapToColdOpen = enabled },
            "chapter-enhancement" => new PluginConfiguration { EnableChapterAnalyzer = false, EnhanceChapterCredits = enabled },
            _ => throw new ArgumentOutOfRangeException(nameof(setting)),
        };

        Assert.Equal(
            ConfigHasher.Analysis(Configure(setting, true), mode, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(Configure(setting, false), mode, AnalyzerAction.Default, ffmpegValid: true));
    }

    /// <summary>
    /// The default configuration must keep the hash it had before the detection method switches
    /// existed, in every mode, or upgrading re-analyzes the whole library. These are the values
    /// releases without the switches wrote; the tokens are emitted only when a method is off.
    /// </summary>
    [Fact]
    public void DefaultAnalysisHashes_ArePinnedAcrossEveryMode()
    {
        var defaults = new PluginConfiguration();

        var actual = string.Join(
            ",",
            Enum.GetValues<AnalysisMode>().Select(mode =>
                $"{mode}={ConfigHasher.Analysis(defaults, mode, AnalyzerAction.Default, ffmpegValid: true)}"));

        Assert.Equal(
            "Introduction=6908FA7CE6CEDB25,Credits=353008E48A5F559E,Preview=19C776EE18C06441,Recap=9F39FAF9AB4D7445,Commercial=C5F494E8A415EC4F",
            actual);
    }

    /// <summary>
    /// A detection method reaches only the modes it runs in, so switching one off invalidates
    /// exactly those modes' analysis and leaves the rest alone. Detection cache rows never move:
    /// a method switched back on reuses its cached scans instead of decoding again.
    /// </summary>
    [Theory]
    [InlineData(AnalysisMode.Introduction, true, true, false)]
    [InlineData(AnalysisMode.Credits, true, true, true)]
    [InlineData(AnalysisMode.Recap, true, true, true)]
    [InlineData(AnalysisMode.Preview, true, false, false)]
    [InlineData(AnalysisMode.Commercial, true, false, false)]
    public void DetectionMethod_InvalidatesOnlyTheModesItRunsIn(AnalysisMode mode, bool chapter, bool chromaprint, bool keyframe)
    {
        var defaults = new PluginConfiguration();
        var baseline = ConfigHasher.Analysis(defaults, mode, AnalyzerAction.Default, ffmpegValid: true);
        (PluginConfiguration Config, bool Invalidates)[] methods =
        [
            (new PluginConfiguration { EnableChapterAnalyzer = false }, chapter),
            (new PluginConfiguration { EnableChromaprintAnalyzer = false }, chromaprint),
            (new PluginConfiguration { EnableKeyframeAnalyzer = false }, keyframe),
        ];

        foreach (var (config, invalidates) in methods)
        {
            Assert.Equal(invalidates, baseline != ConfigHasher.Analysis(config, mode, AnalyzerAction.Default, ffmpegValid: true));
            foreach (var type in Enum.GetValues<CacheEntryType>())
            {
                Assert.Equal(ConfigHasher.DetectionCache(defaults, type, mode), ConfigHasher.DetectionCache(config, type, mode));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Hash_ChangesOnlyForRelevantSettings(string name, string first, string second, bool expectEqual)
    {
        Assert.NotEmpty(name);
        if (expectEqual)
        {
            Assert.Equal(first, second);
        }
        else
        {
            Assert.NotEqual(first, second);
        }
    }
}
