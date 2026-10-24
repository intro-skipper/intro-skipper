// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
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

    // Settings that change no analysis hash: scheduling, scope, storage, serving, playback UI
    // and process limits.
    private static readonly HashSet<string> NotDetection =
    [
        nameof(PluginConfiguration.AutoDetectIntros),
        nameof(PluginConfiguration.AnalysisPaused),
        nameof(PluginConfiguration.RemoteAnalysisEnabled),
        nameof(PluginConfiguration.RemoteCreditsProfileHash),
        nameof(PluginConfiguration.RemoteAnalysisLeaseMinutes),
        nameof(PluginConfiguration.SettledSeasonDelayHours),
        nameof(PluginConfiguration.AnalyzeSeasonZero),
        nameof(PluginConfiguration.CacheCompressionLevel),
        nameof(PluginConfiguration.UpdateMediaSegments),
        nameof(PluginConfiguration.ScanIntroduction),
        nameof(PluginConfiguration.ScanCredits),
        nameof(PluginConfiguration.ScanRecap),
        nameof(PluginConfiguration.ScanPreview),
        nameof(PluginConfiguration.ScanCommercial),
        nameof(PluginConfiguration.FirstEpisodeIntroMode),
        nameof(PluginConfiguration.SkipFirstEpisode),
        nameof(PluginConfiguration.SkipFirstEpisodeAnime),
        nameof(PluginConfiguration.MaxParallelism),
        nameof(PluginConfiguration.ProcessThreads),
        nameof(PluginConfiguration.ScanTimeoutSeconds),
        nameof(PluginConfiguration.ProcessPriority),
        nameof(PluginConfiguration.UseFileTransformationPlugin),
        nameof(PluginConfiguration.SkipbuttonHideDelay),
        nameof(PluginConfiguration.AutoSkipIntro),
        nameof(PluginConfiguration.AutoSkipCredits),
        nameof(PluginConfiguration.SkipButtonVisibleSeconds),
        nameof(PluginConfiguration.EnableMainMenu),
    ];

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
        Case("Credits BlackFrame cache invalidates pre-keyframe-filter rows", Cache(defaults, CacheEntryType.BlackFrame, AnalysisMode.Credits), "3C80558124EB7712", false);
        Case("Credits KeyframeVisual cache invalidates pre-keyframe-filter rows", Cache(defaults, CacheEntryType.KeyframeVisual, AnalysisMode.Credits), "587D32B8BFB41219", false);
        Case("Credits KeyframeVisual cache invalidates rows without the frame difference", Cache(defaults, CacheEntryType.KeyframeVisual, AnalysisMode.Credits), "0F5DAFC545AB413F", false);
        Case("Credits BlackInterval cache invalidates rows probed under tinted-zeroed thresholds", Cache(defaults, CacheEntryType.BlackInterval, AnalysisMode.Credits), "3BB44B93C34D3451", false);

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

        // Pin the analysis identity for a preferred language under the configured stream-selection policy.
        Case("Introduction analysis with a preferred language is pinned", Analysis(english, AnalysisMode.Introduction), "BC177183ECC85CBA", true);

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
        // The credits pass reads none of these, so changing one must not rerun Credits.
        Case("Credits analysis ignores PreferChromaprint", Analysis(defaults, AnalysisMode.Credits), Analysis(new PluginConfiguration { PreferChromaprint = true }, AnalysisMode.Credits), true);
        Case("Credits analysis ignores the analysis percentage", Analysis(defaults, AnalysisMode.Credits), Analysis(new PluginConfiguration { AnalysisPercent = 40 }, AnalysisMode.Credits), true);
        Case("Credits analysis ignores the intro minimum", Analysis(defaults, AnalysisMode.Credits), Analysis(new PluginConfiguration { MinimumIntroDuration = 30 }, AnalysisMode.Credits), true);
        Case("Credits analysis ignores black-frame chapter markers outside the legacy analyzer", Analysis(defaults, AnalysisMode.Credits), Analysis(new PluginConfiguration { UseChapterMarkersBlackFrame = false }, AnalysisMode.Credits), true);
        Case("Credits analysis changes with black-frame chapter markers under the legacy analyzer", Analysis(new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true }, AnalysisMode.Credits), Analysis(new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true, UseChapterMarkersBlackFrame = false }, AnalysisMode.Credits), false);
        // A preview chapter after a credits chapter tells whether story follows the chapter.
        Case("Credits analysis changes with the preview chapter pattern", Analysis(defaults, AnalysisMode.Credits), Analysis(new PluginConfiguration { ChapterAnalyzerPreviewPattern = "Next" }, AnalysisMode.Credits), false);
        Case("Credits analysis changes with the preview minimum", Analysis(defaults, AnalysisMode.Credits), Analysis(new PluginConfiguration { MinimumPreviewDuration = 30 }, AnalysisMode.Credits), false);
        Case("Credits analysis changes with the preview maximum", Analysis(defaults, AnalysisMode.Credits), Analysis(new PluginConfiguration { MaximumPreviewDuration = 60 }, AnalysisMode.Credits), false);
        Case("Credits analysis ignores the preview minimum under chapter enhancement", Analysis(new PluginConfiguration { EnhanceChapterCredits = true }, AnalysisMode.Credits), Analysis(new PluginConfiguration { EnhanceChapterCredits = true, MinimumPreviewDuration = 30 }, AnalysisMode.Credits), true);
        Case("Credits analysis ignores the preview chapter pattern under a BlackFrame action", ConfigHasher.Analysis(defaults, AnalysisMode.Credits, AnalyzerAction.BlackFrame, ffmpegValid: true), ConfigHasher.Analysis(new PluginConfiguration { ChapterAnalyzerPreviewPattern = "Next" }, AnalysisMode.Credits, AnalyzerAction.BlackFrame, ffmpegValid: true), true);
        // Full-length chapters replace the preview duration limits; the pattern still applies.
        Case("Credits analysis ignores the preview duration limits under full-length chapters", Analysis(new PluginConfiguration { FullLengthChapters = true }, AnalysisMode.Credits), Analysis(new PluginConfiguration { FullLengthChapters = true, MinimumPreviewDuration = 30, MaximumPreviewDuration = 60 }, AnalysisMode.Credits), true);
        Case("Credits analysis changes with the preview chapter pattern under full-length chapters", Analysis(new PluginConfiguration { FullLengthChapters = true }, AnalysisMode.Credits), Analysis(new PluginConfiguration { FullLengthChapters = true, ChapterAnalyzerPreviewPattern = "Next" }, AnalysisMode.Credits), false);
        // Installs from before 10.11.22 still carry that release's default preview pattern.
        Case("Credits analysis keeps the pinned hash under the pre-10.11.22 default preview pattern", Analysis(new PluginConfiguration { ChapterAnalyzerPreviewPattern = @"(^|\s)(Preview|PV|Sneak\s?Peek|Coming\s?(Up|Soon)|Next\s+(time|on|episode)|Extra|Teaser|Trailer)(?!\sEnd)(\s|:|$)" }, AnalysisMode.Credits), "353008E48A5F559E", true);
        Case("Preview analysis changes with the preview minimum", Analysis(defaults, AnalysisMode.Preview), Analysis(new PluginConfiguration { MinimumPreviewDuration = 30 }, AnalysisMode.Preview), false);
        Case("Recap analysis changes with chromaprint availability", Analysis(defaults, AnalysisMode.Recap), Analysis(defaults, AnalysisMode.Recap, ffmpegValid: false), false);
        Case("Preview analysis ignores chromaprint availability", Analysis(defaults, AnalysisMode.Preview), Analysis(defaults, AnalysisMode.Preview, ffmpegValid: false), true);
        Case("Commercial analysis ignores chromaprint availability", Analysis(defaults, AnalysisMode.Commercial), Analysis(defaults, AnalysisMode.Commercial, ffmpegValid: false), true);

        // Frozen to what 12.0.5 wrote for seasons that do not derive previews, so with subtitle
        // detection off they do not re-analyze Preview on upgrade. Seasons that derive previews
        // carry the anchor token and re-derive once (12.0.5 wrote 2735F6E29B514900 for them).
        // Enabling subtitle detection adds its own hash.
        Case("Preview analysis hash is pinned while subtitle detection is off", Analysis(defaults, AnalysisMode.Preview), "19C776EE18C06441", true);
        Case("Preview analysis re-derives under the last-credits anchor", Analysis(new PluginConfiguration { AnimePreviewFromCreditsEnd = true }, AnalysisMode.Preview), "EEAC9F1E9F57CD47", true);

        // 12.0.5 wrote 9F39FAF9AB4D7445 here, when a Chromaprint recap ended at the latest black
        // frame. The sting end token reruns Recap once in every season.
        Case("Recap analysis hash is pinned while subtitle detection is off", Analysis(defaults, AnalysisMode.Recap), "584E57B3CA358E11", true);
        Case("Recap analysis ignores the subtitle pattern while detection is off", Analysis(defaults, AnalysisMode.Recap), Analysis(new PluginConfiguration { SubtitleRecapPattern = "previously" }, AnalysisMode.Recap), true);
        Case("Preview analysis ignores the subtitle pattern while detection is off", Analysis(defaults, AnalysisMode.Preview), Analysis(new PluginConfiguration { SubtitlePreviewPattern = "preview" }, AnalysisMode.Preview), true);

        // With subtitle detection on, the hash is "subtitle-v1:", the conventional hash, ":" and a
        // hash of every setting. The pins keep a refactor of the hasher from changing what a
        // stored record means. A blank pattern detects nothing, so it hashes as detection off.
        Case("Recap analysis hash is pinned while subtitle detection is on", Analysis(new PluginConfiguration { EnableSubtitleRecapDetection = true }, AnalysisMode.Recap), "subtitle-v1:584E57B3CA358E11:034582192926BF24", true);
        Case("Preview analysis hash is pinned while subtitle detection is on", Analysis(new PluginConfiguration { EnableSubtitlePreviewDetection = true }, AnalysisMode.Preview), "subtitle-v1:19C776EE18C06441:ED1EF897C859EA54", true);
        Case("Recap analysis treats a blank subtitle pattern as detection off", Analysis(new PluginConfiguration { EnableSubtitleRecapDetection = true, SubtitleRecapPattern = " " }, AnalysisMode.Recap), Analysis(defaults, AnalysisMode.Recap), true);
        Case("Preview analysis treats a blank subtitle pattern as detection off", Analysis(new PluginConfiguration { EnableSubtitlePreviewDetection = true, SubtitlePreviewPattern = string.Empty }, AnalysisMode.Preview), Analysis(defaults, AnalysisMode.Preview), true);

        // The subtitle languages reopen only the modes whose subtitle detection reads them,
        // and any ISO 639 form of a language hashes the same.
        var recapSubtitles = new PluginConfiguration { EnableSubtitleRecapDetection = true };
        var recapSubtitlesGermanEnglish = new PluginConfiguration { EnableSubtitleRecapDetection = true, SubtitleLanguages = "ger, eng" };
        Case("Recap analysis changes with the subtitle languages while detection is on", Analysis(recapSubtitles, AnalysisMode.Recap), Analysis(recapSubtitlesGermanEnglish, AnalysisMode.Recap), false);
        Case("Recap analysis normalizes the subtitle languages", Analysis(recapSubtitlesGermanEnglish, AnalysisMode.Recap), Analysis(new PluginConfiguration { EnableSubtitleRecapDetection = true, SubtitleLanguages = "en,DEU" }, AnalysisMode.Recap), true);
        Case("Preview analysis ignores the subtitle languages while its detection is off", Analysis(recapSubtitles, AnalysisMode.Preview), Analysis(recapSubtitlesGermanEnglish, AnalysisMode.Preview), true);

        return data;
    }

    [Fact]
    public void CreditsAnalysis_IgnoresSelectedRemoteProfilePin()
    {
        var first = new PluginConfiguration
        {
            RemoteAnalysisEnabled = true,
            RemoteAnalysisModes = [AnalysisMode.Credits],
            RemoteCreditsProfileHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        };
        var second = new PluginConfiguration
        {
            RemoteAnalysisEnabled = true,
            RemoteAnalysisModes = [AnalysisMode.Credits],
            RemoteCreditsProfileHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        };

        Assert.Equal(
            ConfigHasher.Analysis(first, AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: true),
            ConfigHasher.Analysis(second, AnalysisMode.Credits, AnalyzerAction.Default, ffmpegValid: true));
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

    /// <summary>
    /// Every mode reads chapters, chapter snapping even under a credits BlackFrame action, so
    /// every record names its chapter source. The detection cache holds media evidence with no
    /// chapter input, so an episode the chapter source check reruns reads it again. The pinned
    /// hashes above show that the setting's default leaves every hash as it was.
    /// </summary>
    [Theory]
    [InlineData(AnalysisMode.Introduction, AnalyzerAction.Default)]
    [InlineData(AnalysisMode.Credits, AnalyzerAction.Default)]
    [InlineData(AnalysisMode.Credits, AnalyzerAction.BlackFrame)]
    [InlineData(AnalysisMode.Recap, AnalyzerAction.Default)]
    [InlineData(AnalysisMode.Preview, AnalyzerAction.Default)]
    [InlineData(AnalysisMode.Commercial, AnalyzerAction.Default)]
    public void ChapterSource_InvalidatesAnalysisButNotTheDetectionCache(AnalysisMode mode, AnalyzerAction action)
    {
        var defaults = new PluginConfiguration();
        var embedded = new PluginConfiguration { UseEmbeddedChaptersForAnalysis = true };

        Assert.NotEqual(ConfigHasher.Analysis(defaults, mode, action, true), ConfigHasher.Analysis(embedded, mode, action, true));
        foreach (var type in Enum.GetValues<CacheEntryType>())
        {
            Assert.Equal(ConfigHasher.DetectionCache(defaults, type, mode), ConfigHasher.DetectionCache(embedded, type, mode));
        }
    }

    /// <summary>
    /// The chapter source sits in the conventional part of a compound hash. A switch reruns the
    /// chapter analyzer first, which a subtitle-only rerun would skip.
    /// </summary>
    [Theory]
    [InlineData(AnalysisMode.Recap, false)]
    [InlineData(AnalysisMode.Recap, true)]
    [InlineData(AnalysisMode.Preview, false)]
    [InlineData(AnalysisMode.Preview, true)]
    public void ChapterSourceSwitch_IsNotASubtitleOnlyChange(AnalysisMode mode, bool embeddedBefore)
    {
        static PluginConfiguration Config(bool embedded) => new()
        {
            EnableSubtitleRecapDetection = true,
            EnableSubtitlePreviewDetection = true,
            UseEmbeddedChaptersForAnalysis = embedded,
        };

        var stored = ConfigHasher.Analysis(Config(embeddedBefore), mode, AnalyzerAction.Default, true);
        var conventional = ConfigHasher.Analysis(Config(!embeddedBefore), mode, AnalyzerAction.Default, true, includeSubtitleSettings: false);

        Assert.False(ConfigHasher.IsSubtitleOnlyHashChange(stored, conventional));
    }

    [Fact]
    public void SettledConsensusHash_PreservesDisabledHashAndTracksEnabledAlgorithm()
    {
        static string Hash(PluginConfiguration configuration)
            => ConfigHasher.Analysis(configuration, AnalysisMode.Introduction, AnalyzerAction.Default, ffmpegValid: true);

        var disabled = new PluginConfiguration
        {
            ReanalyzeSettledSeasons = true,
            UseSettledSeasonChromaprintConsensus = false,
        };
        var enabled = new PluginConfiguration
        {
            ReanalyzeSettledSeasons = true,
            UseSettledSeasonChromaprintConsensus = true,
        };
        var changedMinimum = new PluginConfiguration
        {
            ReanalyzeSettledSeasons = true,
            UseSettledSeasonChromaprintConsensus = true,
            SettledSeasonChromaprintConsensusEpisodes = PluginConfiguration.DefaultSettledSeasonChromaprintConsensusEpisodes + 1,
        };
        var reanalysisOff = new PluginConfiguration
        {
            ReanalyzeSettledSeasons = false,
            UseSettledSeasonChromaprintConsensus = true,
        };

        Assert.Equal(Hash(new PluginConfiguration()), Hash(disabled));
        Assert.Equal(Hash(reanalysisOff), Hash(new PluginConfiguration { ReanalyzeSettledSeasons = false, UseSettledSeasonChromaprintConsensus = false }));
        Assert.NotEqual(Hash(disabled), Hash(enabled));
        Assert.NotEqual(Hash(enabled), Hash(changedMinimum));
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
    /// Every scalar persisted setting (bool, number, string or enum) either reaches some
    /// analysis hash or is listed in <see cref="NotDetection"/>, so a new setting cannot change
    /// detection output while seasons keep results computed under its old value.
    /// </summary>
    [Fact]
    public void EverySetting_ChangesSomeAnalysisHash_ExactlyWhenItIsDetection()
    {
        Func<PluginConfiguration>[] bases =
        [
            () => new PluginConfiguration(),
            // Subtitle patterns and languages count only while subtitle detection is on.
            () => new PluginConfiguration { EnableSubtitleRecapDetection = true, EnableSubtitlePreviewDetection = true },
            // The chapter-marker toggle counts only for the legacy analyzer.
            () => new PluginConfiguration { UseLegacyBlackFrameAnalyzer = true },
            // The consensus settings count only while settled seasons are re-analyzed.
            () => new PluginConfiguration { ReanalyzeSettledSeasons = true },
        ];

        var settings = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(XmlIgnoreAttribute)))
            .Where(p => p.PropertyType.IsValueType || p.PropertyType == typeof(string));

        var hashedButListed = new List<string>();
        var unhashedAndUnlisted = new List<string>();
        foreach (var setting in settings)
        {
            var hashed = bases.Any(create => ChangesSomeHash(create, setting));
            var listed = NotDetection.Contains(setting.Name);
            if (hashed && listed)
            {
                hashedButListed.Add(setting.Name);
            }
            else if (!hashed && !listed)
            {
                unhashedAndUnlisted.Add(setting.Name);
            }
        }

        Assert.True(
            hashedButListed.Count == 0 && unhashedAndUnlisted.Count == 0,
            $"Hashed but listed as not detection: [{string.Join(", ", hashedButListed)}]. Not hashed and not listed: [{string.Join(", ", unhashedAndUnlisted)}].");

        static bool ChangesSomeHash(Func<PluginConfiguration> create, PropertyInfo setting)
        {
            var config = create();
            var before = Hashes(config);
            var original = setting.GetValue(config);
            setting.SetValue(config, Perturb(setting, original));
            Assert.False(Equals(original, setting.GetValue(config)), $"{setting.Name} reads back unchanged after perturbing it.");
            return !before.SequenceEqual(Hashes(config));
        }

        static string[] Hashes(PluginConfiguration config)
            => [.. from mode in Enum.GetValues<AnalysisMode>()
                   from action in Enum.GetValues<AnalyzerAction>()
                   select ConfigHasher.Analysis(config, mode, action, ffmpegValid: true)];

        // The getter maps an unknown first-episode mode back to Analyze, so it needs a real value.
        static object Perturb(PropertyInfo setting, object? value)
            => setting.Name == nameof(PluginConfiguration.FirstEpisodeIntroMode)
                ? PluginConfiguration.FirstEpisodeIntroModeIgnore
                : value switch
                {
                    bool flag => !flag,
                    int number => number + 1,
                    double number => number + 0.5,
                    string text => text + "x",
                    Enum member => NextMember(member),
                    _ => throw new InvalidOperationException($"No perturbation for {setting.Name} ({setting.PropertyType})."),
                };

        // The next declared value, not value + 1: ProcessPriorityClass values are sparse.
        static object NextMember(Enum member)
        {
            var members = Enum.GetValues(member.GetType());
            return members.GetValue((Array.IndexOf(members, member) + 1) % members.Length)!;
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
