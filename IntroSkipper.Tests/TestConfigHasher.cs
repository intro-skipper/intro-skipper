// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

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

        Case("Chromaprint cache changes with preferred language", Cache(defaults, CacheEntryType.Chromaprint, AnalysisMode.Introduction), Cache(english, CacheEntryType.Chromaprint, AnalysisMode.Introduction), false);
        Case("Chromaprint cache normalizes preferred language", Cache(english, CacheEntryType.Chromaprint, AnalysisMode.Introduction), Cache(englishPadded, CacheEntryType.Chromaprint, AnalysisMode.Introduction), true);
        Case("Chromaprint cache changes with stream selection policy", Cache(mostChannels, CacheEntryType.Chromaprint, AnalysisMode.Introduction), Cache(lowestIndex, CacheEntryType.Chromaprint, AnalysisMode.Introduction), false);
        Case("Chromaprint stream identity reuses across selection settings", Cache(englishMostChannels, CacheEntryType.Chromaprint, AnalysisMode.Introduction, MostChannelsStreamCacheVariant), Cache(lowestIndex, CacheEntryType.Chromaprint, AnalysisMode.Introduction, MostChannelsStreamCacheVariant), true);

        Case("Analysis changes with stream selection policy", Analysis(mostChannels, AnalysisMode.Introduction), Analysis(lowestIndex, AnalysisMode.Introduction), false);
        Case("Introduction analysis changes with preferred language", Analysis(defaults, AnalysisMode.Introduction), Analysis(english, AnalysisMode.Introduction), false);
        Case("Credits analysis changes with preferred language", Analysis(defaults, AnalysisMode.Credits), Analysis(english, AnalysisMode.Credits), false);
        Case("Recap analysis changes with preferred language", Analysis(defaults, AnalysisMode.Recap), Analysis(english, AnalysisMode.Recap), false);

        // Toggling the non-black fallback changes credits output (when its analyzer is active), so it
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
        Case("Recap analysis changes with chromaprint availability", Analysis(defaults, AnalysisMode.Recap), Analysis(defaults, AnalysisMode.Recap, ffmpegValid: false), false);
        Case("Preview analysis ignores chromaprint availability", Analysis(defaults, AnalysisMode.Preview), Analysis(defaults, AnalysisMode.Preview, ffmpegValid: false), true);
        Case("Commercial analysis ignores chromaprint availability", Analysis(defaults, AnalysisMode.Commercial), Analysis(defaults, AnalysisMode.Commercial, ffmpegValid: false), true);

        return data;
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
