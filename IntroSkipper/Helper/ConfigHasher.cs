// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Helper;

/// <summary>
/// Computes deterministic hashes for the configuration subsets that affect analysis output.
/// </summary>
internal static class ConfigHasher
{
    /// <summary>
    /// Prefix marking a detection cache hash that is scoped to an effective audio stream.
    /// Frozen: rows carrying it are already on disk.
    /// </summary>
    public const string StreamScopedDetectionCacheHashPrefix = "audio-stream-v1|";

    /// <summary>
    /// Cache identity used when FFmpeg's own audio pick is the effective stream: the default-disposition
    /// stream, then most channels, then lowest index. Frozen: rows carrying it are already on disk.
    /// </summary>
    public const string DefaultAudioStreamCacheVariant = "policy=most-channels";

    private const string ChapterBeforeSubtitlePrecedenceToken = "|chapterBeforeSubtitle=v1";
    private const string SubtitleHashPrefix = "subtitle-v1:";

    // Credits-derived previews run from the end of the last credits run to the episode end;
    // 12.0.5 ran them from the first run to the next. Present only while the season derives
    // previews, so those seasons re-derive once and no other season reopens. It sits in the
    // conventional part of a compound hash, outside the subtitle settings.
    private const string PreviewAnchorToken = "|previewAnchor=lastCredits";

    // Subtitle recaps end at a chapter or a black run of at least 2 s, no longer at the intro
    // start (SubtitleAnalyzer). It sits in the subtitle part of the Recap hash, so seasons with
    // subtitle recap detection on rerun subtitle analysis alone. Bump it when that rule changes.
    private const string SubtitleRecapEndToken = "|subtitleRecapEnd=black2";

    // Chromaprint recaps end at the first black run of at least 2 s after the sting, no longer at
    // the latest black frame before the boundary (RecapDetectionHelper.BuildRecapFromSting). It
    // sits in the conventional part of every Recap hash, so each season reruns Recap once; the
    // fingerprints and the black-frame scan come from the detection cache. Bump it when that rule
    // changes. The token also covers which stings count at all: one must reach the minimum length
    // and be shared by enough episodes without being shared by the whole season
    // (ChromaprintAnalyzer.FilterRecapBySeasonSupport).
    private const string RecapStingEndToken = "|stingEnd=black2|stingSupport=2|stingMin=4";

    // With UseEmbeddedChaptersForAnalysis on, analysis reads the chapters embedded in the media
    // file instead of Jellyfin's chapter table. Present only while the setting is on, so the
    // default configuration keeps its hashes. Every mode carries it under every action: the
    // chapter analyzer serves all five modes, and chapter-boundary snapping, the subtitle recap
    // end and the legacy black-frame chapter markers read chapters too, snapping even under a
    // credits BlackFrame action. The token names the source a record was computed from; a switch
    // alone reruns nothing, because the season pass compares the two sources' chapters first and
    // re-stamps a record whose sources agree (QueueVerifier, BaseItemAnalyzerTask.ReadChaptersAsync).
    // It sits in the conventional part of a compound hash, so a switch is never a subtitle-only
    // change.
    private const string EmbeddedChapterSourceToken = "|chapterSource=embedded";

    // Settled consensus is an opt-in analysis path, so it is hashed only when settled-season
    // re-analysis can use it. Existing hashes stay stable for users who do not enable that path.

    // With a preferred audio language under the most-channels policy, Chromaprint ranks the
    // default-disposition stream first, as ffmpeg does, and maps the chosen stream. It used to rank
    // by channels alone and leave the stream it ranked first unmapped, so ffmpeg could fingerprint
    // another one. Present only in that configuration, so those seasons rerun once and no other
    // season reopens.
    private const string FfmpegStreamRankToken = "|audioRank=ffmpeg";

    // The default preview chapter pattern before 10.11.22. Jellyfin saved the defaults of the
    // first load, so installs from that time still carry it untouched. Frozen.
    private const string LegacyDefaultPreviewPattern = @"(^|\s)(Preview|PV|Sneak\s?Peek|Coming\s?(Up|Soon)|Next\s+(time|on|episode)|Extra|Teaser|Trailer)(?!\sEnd)(\s|:|$)";

    private static readonly PluginConfiguration _defaults = new();

    /// <summary>
    /// Computes a hash for a stored analysis result.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer priority/action used for the season.</param>
    /// <param name="ffmpegValid">Whether the current FFmpeg build supports Chromaprint. Folded into the
    /// hash for Chromaprint-capable modes so a settled <see cref="EpisodeState.NoSegments"/> season is
    /// re-analyzed once when Chromaprint becomes available instead of being skipped forever.</param>
    /// <param name="analysisPercentOverride">Optional season-level percentage override.</param>
    /// <param name="analysisLengthLimitOverride">Optional season-level runtime limit override in minutes.</param>
    /// <param name="previewFromCreditsEndOverride">Optional season-level setting for deriving a Preview segment from Credits.</param>
    /// <param name="subtitleRecapDetectionOverride">Optional season-level setting for subtitle recap detection.</param>
    /// <param name="subtitlePreviewDetectionOverride">Optional season-level setting for subtitle preview detection.</param>
    /// <param name="includeSubtitleSettings">Whether to include subtitle settings in the mode hash. Disabling this returns the conventional-settings hash used to recognize subtitle-only changes.</param>
    /// <param name="chapterSource">The chapter source the analyzed chapters came from, when the season pass read them. <see langword="null"/> takes the setting.</param>
    /// <returns>A compact hash; enabled subtitle detection adds a separate subtitle component to Recap or Preview.</returns>
    public static string Analysis(
        PluginConfiguration config,
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        int? analysisPercentOverride = null,
        int? analysisLengthLimitOverride = null,
        bool? previewFromCreditsEndOverride = null,
        bool? subtitleRecapDetectionOverride = null,
        bool? subtitlePreviewDetectionOverride = null,
        bool includeSubtitleSettings = true,
        ChapterSource? chapterSource = null)
    {
        // The season pass reads chapters once, so a record names the source they came from,
        // not the setting at the moment the mode ran.
        var source = chapterSource ?? config.ChapterSource;
        var analysisPercent = analysisPercentOverride ?? config.AnalysisPercent;
        var analysisLengthLimit = analysisLengthLimitOverride ?? config.AnalysisLengthLimit;
        var previewFromCreditsEnd = previewFromCreditsEndOverride ?? config.AnimePreviewFromCreditsEnd;
        // Only Recap and Preview have subtitle detection, and a blank pattern detects nothing,
        // so it hashes as detection off.
        var includeSubtitleSettingsForMode = includeSubtitleSettings
            && config.ActiveSubtitlePattern(mode, mode == AnalysisMode.Recap ? subtitleRecapDetectionOverride : subtitlePreviewDetectionOverride) is not null;
        var input = mode switch
        {
            AnalysisMode.Introduction => Invariant(
                $"analysis|v1|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerIntroductionPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct={analysisPercent}|limit={analysisLengthLimit}|min={config.MinimumIntroDuration}|max={config.MaximumIntroDuration}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}{ChromaprintStreamToken(config)}{SettledConsensusToken(config, mode, action, ffmpegValid)}",
                $"{AdjustmentHash(config)}{ChapterSourceToken(source)}"),

            // Frozen at v3 on purpose: the credits pass replaced the first-wins chain
            // without a bump, because a bump re-fingerprints and re-scans every season a chapter
            // or black frame settled. Seasons analyzed by the chain keep their result until they
            // are rescanned. The pass does not read PreferChromaprint, AnalysisPercent or
            // MinimumIntroDuration, so their tokens keep the defaults' literals, and the
            // chapter-marker toggle counts only for the legacy analyzer that reads it. A
            // pinned-hash test guards the string.
            AnalysisMode.Credits => Invariant(
                $"analysis|v3|mode={mode}|action={action}|prefer=False|chap={config.ChapterAnalyzerEndCreditsPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct=25|maxCredits={config.MaximumCreditsDuration}|maxMovie={config.MaximumMovieCreditsDuration}|probe={config.ProbeAudioDuration}|minRegion=15",
                $"|min={config.MinimumCreditsDuration}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}|bfchap={!config.UseLegacyBlackFrameAnalyzer || config.UseChapterMarkersBlackFrame}",
                $"|bflegacy={config.UseLegacyBlackFrameAnalyzer}|bfrefine={config.RefineCreditsBoundary}|bfVersion=3{CreditsNonBlackToken(config)}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}{ChromaprintStreamToken(config)}{SettledConsensusToken(config, mode, action, ffmpegValid)}",
                $"|animePreview={previewFromCreditsEnd}{ChapterEnhancementToken(config, action)}{PreviewChapterToken(config, action)}",
                $"{AdjustmentHash(config)}{ChapterSourceToken(source)}"),

            AnalysisMode.Recap => Invariant(
                $"analysis|v3|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerRecapPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumRecapDuration}|max={config.MaximumRecapDuration}{(includeSubtitleSettingsForMode ? ChapterBeforeSubtitlePrecedenceToken : string.Empty)}",
                $"|detMin={config.MinimumRecapDetectionDuration}|detMax={config.MaximumRecapDetectionDuration}",
                $"|recapBlackFrames={config.DetectRecapUsingBlackFrames}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}{RecapColdOpenToken(config)}{RecapStingEndToken}",
                $"{(includeSubtitleSettingsForMode ? $"|subtitle=True|subtitlePattern={config.SubtitleRecapPattern}|subtitleLanguages={SubtitleLanguageSelection.Parse(config.SubtitleLanguages).Normalized}{SubtitleRecapEndToken}" : string.Empty)}",
                $"|pct={analysisPercent}|limit={analysisLengthLimit}|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}{ChromaprintStreamToken(config)}",
                $"{AdjustmentHash(config)}{ChapterSourceToken(source)}"),

            AnalysisMode.Preview => Invariant(
                $"analysis|v2|mode={mode}|action={action}|chap={config.ChapterAnalyzerPreviewPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumPreviewDuration}|max={config.MaximumPreviewDuration}{(includeSubtitleSettingsForMode ? ChapterBeforeSubtitlePrecedenceToken : string.Empty)}",
                $"|animePreview={previewFromCreditsEnd}{(previewFromCreditsEnd ? PreviewAnchorToken : string.Empty)}{(includeSubtitleSettingsForMode ? $"|subtitle=True|subtitlePattern={config.SubtitlePreviewPattern}|subtitleLanguages={SubtitleLanguageSelection.Parse(config.SubtitleLanguages).Normalized}" : string.Empty)}",
                $"{AdjustmentHash(config)}{ChapterSourceToken(source)}"),

            AnalysisMode.Commercial => Invariant(
                $"analysis|v1|mode={mode}|action={action}|chap={config.ChapterAnalyzerCommercialPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumCommercialDuration}|max={config.MaximumCommercialDuration}",
                $"{AdjustmentHash(config)}{ChapterSourceToken(source)}"),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        var hash = ComputeHash(input);
        if (!includeSubtitleSettingsForMode)
        {
            return hash;
        }

        var conventionalHash = Analysis(
            config,
            mode,
            action,
            ffmpegValid,
            analysisPercentOverride,
            analysisLengthLimitOverride,
            previewFromCreditsEndOverride,
            subtitleRecapDetectionOverride,
            subtitlePreviewDetectionOverride,
            includeSubtitleSettings: false,
            chapterSource: source);
        return $"{SubtitleHashPrefix}{conventionalHash}:{hash}";
    }

    /// <summary>
    /// Determines whether a stored compound Recap or Preview hash has the same conventional
    /// analyzer inputs as the current configuration, ignoring only subtitle settings.
    /// </summary>
    /// <param name="storedHash">Previously stored analysis hash.</param>
    /// <param name="conventionalHash">Current hash computed without subtitle settings.</param>
    /// <returns>Whether only the subtitle settings changed.</returns>
    public static bool IsSubtitleOnlyHashChange(string storedHash, string conventionalHash)
    {
        if (string.Equals(storedHash, conventionalHash, StringComparison.Ordinal))
        {
            return true;
        }

        if (!storedHash.StartsWith(SubtitleHashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = storedHash.IndexOf(':', SubtitleHashPrefix.Length);
        return separatorIndex > SubtitleHashPrefix.Length
            && string.Equals(storedHash[SubtitleHashPrefix.Length..separatorIndex], conventionalHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes a hash for FFmpeg detection cache rows.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>A compact hex hash.</returns>
    public static string DetectionCache(PluginConfiguration config, CacheEntryType type, AnalysisMode mode)
        => DetectionCache(config, type, mode, null);

    /// <summary>
    /// Computes a detection cache hash, optionally scoped to audio stream selection.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="audioStreamIdentity">Effective audio stream identity for Chromaprint entries.</param>
    /// <returns>A compact hash for settings-sensitive scans, or a stream-scoped fingerprint key for Chromaprint entries.</returns>
    public static string DetectionCache(
        PluginConfiguration config,
        CacheEntryType type,
        AnalysisMode mode,
        string? audioStreamIdentity)
    {
        ArgumentNullException.ThrowIfNull(config);

        // A fingerprint is the raw description of a media range.  AnalysisPercent,
        // fingerprint comparison tolerances, probe settings and similar options only
        // change how those points are consumed.  The range is already part of the cache
        // key, and the effective audio stream is the only remaining input to the bytes.
        if (type == CacheEntryType.Chromaprint)
        {
            var streamIdentity = string.IsNullOrWhiteSpace(audioStreamIdentity)
                ? DefaultAudioStreamCacheVariant
                : audioStreamIdentity;
            var fingerprintHash = ComputeHash(Invariant(
                $"fingerprint|v1|{type}|{mode}|audioStream={streamIdentity}"));
            return StreamScopedDetectionCacheHashPrefix + FormattableString.Invariant($"{streamIdentity}|{fingerprintHash}");
        }

        var input = type switch
        {
            CacheEntryType.Silence => Invariant(
                $"cache|v1|{type}|noise={config.SilenceDetectionMaximumNoise}|dur={config.SilenceDetectionMinimumDuration}"),

            // Credits v2: VP9 black-frame scans select packet keyframes.
            CacheEntryType.BlackFrame => Invariant(
                $"cache|v{(mode == AnalysisMode.Credits ? 2 : 1)}|{type}|{mode}|threshold={config.BlackFrameThreshold}{BlackFrameAmountToken(mode)}"),

            CacheEntryType.BlackInterval => Invariant(
                $"cache|v1|{type}|{mode}|blackdetect=v1|threshold={config.BlackFrameThreshold}|bfmin={config.BlackFrameMinimumPercentage}|duration={BlackInterval.MinimumDetectionDuration}"),

            // v2: VP9 keyframe listings select packet keyframes.
            CacheEntryType.Keyframe => $"cache|v2|{type}",

            // Credits v4: VP9 visual scans select packet keyframes.
            CacheEntryType.KeyframeVisual => $"cache|v{(mode == AnalysisMode.Credits ? 4 : 3)}|{type}|{mode}",

            // The probe's width, tolerance and rule are constants: bump the token when they change.
            CacheEntryType.LeadIn => $"cache|v1|{type}",

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

        var hash = ComputeHash(input);
        return hash;
    }

    /// <summary>
    /// Gets a value indicating whether a stream-scoped hash belongs to the requested
    /// effective stream. The hash suffix is deliberately ignored so rows written by
    /// releases whose fingerprint hash also included processing settings remain usable.
    /// </summary>
    /// <param name="cacheHash">Stored cache hash.</param>
    /// <param name="audioStreamIdentity">Effective stream identity, or <see langword="null"/> for FFmpeg's default.</param>
    /// <returns><see langword="true"/> when the stored row uses the same effective stream.</returns>
    public static bool IsStreamScopedDetectionCacheHashFor(string? cacheHash, string? audioStreamIdentity)
    {
        if (!TryGetStreamScopedDetectionCacheVariant(cacheHash, out var storedVariant))
        {
            return false;
        }

        var expectedVariant = string.IsNullOrWhiteSpace(audioStreamIdentity)
            ? DefaultAudioStreamCacheVariant
            : audioStreamIdentity;
        return string.Equals(storedVariant, expectedVariant, StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes the cache hash that was written before audio stream selection existed, when
    /// fingerprints always came from FFmpeg's default stream. Rows carrying it stay readable
    /// whenever the effective stream is still FFmpeg's default, so already-analyzed episodes
    /// keep their place in the Chromaprint comparison pool after an upgrade.
    /// </summary>
    /// <remarks>
    /// WARNING: never modify this input string. It is frozen to what older releases wrote;
    /// it remains an explicit compatibility path for their default-stream rows. A pinned-hash
    /// test guards it.
    /// </remarks>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>The legacy default-selection cache hash.</returns>
    public static string LegacyChromaprintCacheWithoutLanguage(PluginConfiguration config, AnalysisMode mode)
        => ComputeHash(Invariant(
            $"cache|v1|{CacheEntryType.Chromaprint}|{mode}|pct={config.AnalysisPercent}|limit={config.AnalysisLengthLimit}|maxCredits={config.MaximumCreditsDuration}|maxMovie={config.MaximumMovieCreditsDuration}|probe={config.ProbeAudioDuration}"));

    /// <summary>
    /// Gets a value indicating whether a cache hash is scoped to an effective audio stream.
    /// </summary>
    /// <param name="cacheHash">Cache hash to inspect.</param>
    /// <returns><see langword="true"/> when the hash includes an audio stream identity.</returns>
    public static bool IsStreamScopedDetectionCacheHash(string? cacheHash)
        => cacheHash?.StartsWith(StreamScopedDetectionCacheHashPrefix, StringComparison.Ordinal) == true;

    private static bool TryGetStreamScopedDetectionCacheVariant(string? cacheHash, out string variant)
    {
        variant = string.Empty;
        if (!IsStreamScopedDetectionCacheHash(cacheHash))
        {
            return false;
        }

        var value = cacheHash![StreamScopedDetectionCacheHashPrefix.Length..];
        var separator = value.LastIndexOf('|');
        if (separator <= 0)
        {
            return false;
        }

        variant = value[..separator];
        return true;
    }

    /// <summary>
    /// Normalizes the configured preferred audio language (trimmed, lower-cased) so stream
    /// selection and hashing agree on how the value is interpreted.
    /// </summary>
    /// <param name="language">Configured language code.</param>
    /// <returns>The normalized language code, or an empty string when unset.</returns>
    public static string NormalizeAudioLanguage(string? language) => language?.Trim().ToLowerInvariant() ?? string.Empty;

    // DetectNonBlackCredits only affects output when the default analyzer is active; including it
    // unconditionally would invalidate cached credits on the legacy BlackFrameAnalyzer path, which
    // cannot observe the setting (the UI also hides it there).
    private static string CreditsNonBlackToken(PluginConfiguration config)
        => !config.UseLegacyBlackFrameAnalyzer
            ? FormattableString.Invariant($"|nonblack={config.DetectNonBlackCredits}")
            : string.Empty;

    // Only present when enabled so the default-off configuration keeps the hash it had before
    // the option existed and does not re-analyze every recap on upgrade.
    private static string RecapColdOpenToken(PluginConfiguration config)
        => config.AnchorRecapToColdOpen ? "|coldOpen=True" : string.Empty;

    // Only present when enabled so the default-off configuration keeps the hash it had before
    // the option existed. A BlackFrame action restricts the credits pass to that analyzer, which
    // cannot observe the option, so toggling it must not re-scan those seasons.
    private static string ChapterEnhancementToken(PluginConfiguration config, AnalyzerAction action)
        => config.EnhanceChapterCredits && action is not AnalyzerAction.BlackFrame ? "|enhanceChapterCredits=True" : string.Empty;

    // Without enhancement a credits chapter settles its episode only when no story runs to the
    // next preview chapter, so the preview chapter settings can change Credits. Present only when
    // they differ from the defaults, the pre-10.11.22 pattern included, so default installs keep
    // their hash. The two default patterns differ only on colon-delimited end labels such as
    // "Preview: End". Full-length chapters replace the duration limits for every mode, so under
    // them the limits count as their defaults.
    private static string PreviewChapterToken(PluginConfiguration config, AnalyzerAction action)
    {
        if (config.EnhanceChapterCredits || action is AnalyzerAction.BlackFrame)
        {
            return string.Empty;
        }

        var (minimum, maximum) = config.FullLengthChapters
            ? (_defaults.MinimumPreviewDuration, _defaults.MaximumPreviewDuration)
            : (config.MinimumPreviewDuration, config.MaximumPreviewDuration);
        var customPattern = config.ChapterAnalyzerPreviewPattern != _defaults.ChapterAnalyzerPreviewPattern
            && config.ChapterAnalyzerPreviewPattern != LegacyDefaultPreviewPattern;
        return customPattern || minimum != _defaults.MinimumPreviewDuration || maximum != _defaults.MaximumPreviewDuration
            ? FormattableString.Invariant($"|previewChap={config.ChapterAnalyzerPreviewPattern}|previewMin={minimum}|previewMax={maximum}")
            : string.Empty;
    }

    private static string ChapterSourceToken(ChapterSource source)
        => source == ChapterSource.Embedded ? EmbeddedChapterSourceToken : string.Empty;

    // Settled consensus only changes stored output during settled-season re-analysis. Keep its
    // token out of ordinary analysis hashes so changes made while that path is disabled are applied
    // when the setting is later enabled.
    private static string SettledConsensusToken(PluginConfiguration config, AnalysisMode mode, AnalyzerAction action, bool ffmpegValid)
    {
        if (!ffmpegValid ||
            !config.ReanalyzeSettledSeasons ||
            (mode == AnalysisMode.Credits && action == AnalyzerAction.BlackFrame) ||
            !config.UseSettledSeasonChromaprintConsensus)
        {
            return string.Empty;
        }

        return FormattableString.Invariant(
            $"|settledChromaprintConsensus=local-v2:{config.SettledSeasonChromaprintConsensusEpisodes}");
    }

    private static string ChromaprintStreamToken(PluginConfiguration config)
    {
        var language = NormalizeAudioLanguage(config.PreferredAudioLanguage);
        var rank = language.Length > 0 && config.PreferAudioStreamWithMostChannels ? FfmpegStreamRankToken : string.Empty;
        return FormattableString.Invariant($"|audioLanguage={language}|audioMostChannels={config.PreferAudioStreamWithMostChannels}{rank}");
    }

    // The recap black-frame scan reports every frame (blackframe amount=0) so adaptive threshold
    // normalization can observe the full darkness distribution; the token invalidates truncated
    // amount=50 recap rows written before that change without touching other modes' cache rows.
    private static string BlackFrameAmountToken(AnalysisMode mode)
        => mode == AnalysisMode.Recap ? "|amount=0" : string.Empty;

    private static string AdjustmentHash(PluginConfiguration config)
        => Invariant(
            $"|chapAdjust={config.AdjustIntroBasedOnChapters}|silence={config.AdjustIntroBasedOnSilence}|keyframe={config.SnapToKeyframe}",
            $"|endSnap={config.EndSnapThreshold}|winIn={config.AdjustWindowInward}|winOut={config.AdjustWindowOutward}",
            $"|noise={config.SilenceDetectionMaximumNoise}|silDur={config.SilenceDetectionMinimumDuration}",
            $"|startOffset={config.IntroStartOffset}|includeStartOffsetWhenSnapping={config.IncludeIntroStartOffsetWhenSnapping}|endOffset={config.IntroEndOffset}");

    private static string Invariant(params FormattableString[] parts)
        => string.Concat(parts.Select(FormattableString.Invariant));

    private static string ComputeHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 8);
    }
}
