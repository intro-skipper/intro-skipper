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
    /// Cache identity used when FFmpeg's default audio stream is the effective stream.
    /// </summary>
    public const string DefaultAudioStreamCacheVariant = "policy=most-channels";

    private const string ChapterBeforeSubtitlePrecedenceToken = "|chapterBeforeSubtitle=v1";
    private const string SubtitleHashPrefix = "subtitle-v1:";

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
        bool includeSubtitleSettings = true)
    {
        var analysisPercent = analysisPercentOverride ?? config.AnalysisPercent;
        var analysisLengthLimit = analysisLengthLimitOverride ?? config.AnalysisLengthLimit;
        var previewFromCreditsEnd = previewFromCreditsEndOverride ?? config.AnimePreviewFromCreditsEnd;
        var subtitleRecapDetection = subtitleRecapDetectionOverride ?? config.EnableSubtitleRecapDetection;
        var subtitlePreviewDetection = subtitlePreviewDetectionOverride ?? config.EnableSubtitlePreviewDetection;
        var includeSubtitleSettingsForMode = includeSubtitleSettings
            && (mode == AnalysisMode.Recap ? subtitleRecapDetection : mode == AnalysisMode.Preview && subtitlePreviewDetection);
        if (includeSubtitleSettings
            && mode is (AnalysisMode.Recap or AnalysisMode.Preview)
            && !includeSubtitleSettingsForMode)
        {
            return Analysis(
                config,
                mode,
                action,
                ffmpegValid,
                analysisPercentOverride,
                analysisLengthLimitOverride,
                previewFromCreditsEndOverride,
                subtitleRecapDetectionOverride,
                subtitlePreviewDetectionOverride,
                includeSubtitleSettings: false);
        }

        var input = mode switch
        {
            AnalysisMode.Introduction => Invariant(
                $"analysis|v1|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerIntroductionPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct={analysisPercent}|limit={analysisLengthLimit}|min={config.MinimumIntroDuration}|max={config.MaximumIntroDuration}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}{ChromaprintStreamToken(config)}",
                $"{AdjustmentHash(config)}"),

            // Frozen at v3 on purpose: the credits pass replaced the first-wins chain
            // without a bump, because a bump re-fingerprints and re-scans every season a chapter
            // or black frame settled. Seasons analyzed by the chain keep their result until they
            // are rescanned. The prefer token stays for the same reason although the pass does
            // not read PreferChromaprint. A pinned-hash test guards the string.
            AnalysisMode.Credits => Invariant(
                $"analysis|v3|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerEndCreditsPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct={config.AnalysisPercent}|maxCredits={config.MaximumCreditsDuration}|maxMovie={config.MaximumMovieCreditsDuration}|probe={config.ProbeAudioDuration}",
                $"|minRegion={config.MinimumIntroDuration}",
                $"|min={config.MinimumCreditsDuration}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}|bfchap={config.UseChapterMarkersBlackFrame}",
                $"|bflegacy={config.UseLegacyBlackFrameAnalyzer}|bfrefine={config.RefineCreditsBoundary}|bfVersion=3{CreditsNonBlackToken(config)}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}{ChromaprintStreamToken(config)}",
                $"|animePreview={previewFromCreditsEnd}{ChapterEnhancementToken(config, action)}",
                $"{AdjustmentHash(config)}"),

            AnalysisMode.Recap => Invariant(
                $"analysis|v3|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerRecapPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumRecapDuration}|max={config.MaximumRecapDuration}{ChapterBeforeSubtitlePrecedenceToken}",
                $"|detMin={config.MinimumRecapDetectionDuration}|detMax={config.MaximumRecapDetectionDuration}",
                $"|recapBlackFrames={config.DetectRecapUsingBlackFrames}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}{RecapColdOpenToken(config)}",
                $"{(includeSubtitleSettingsForMode ? $"|subtitle={subtitleRecapDetection}|subtitlePattern={config.SubtitleRecapPattern}|subtitleLanguages={SubtitleLanguageSelection.Parse(config.SubtitleLanguages).Normalized}" : string.Empty)}",
                $"|pct={analysisPercent}|limit={analysisLengthLimit}|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}{ChromaprintStreamToken(config)}",
                $"{AdjustmentHash(config)}"),

            AnalysisMode.Preview => Invariant(
                $"analysis|v2|mode={mode}|action={action}|chap={config.ChapterAnalyzerPreviewPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumPreviewDuration}|max={config.MaximumPreviewDuration}{ChapterBeforeSubtitlePrecedenceToken}",
                $"|animePreview={previewFromCreditsEnd}{(includeSubtitleSettingsForMode ? $"|subtitle={subtitlePreviewDetection}|subtitlePattern={config.SubtitlePreviewPattern}|subtitleLanguages={SubtitleLanguageSelection.Parse(config.SubtitleLanguages).Normalized}" : string.Empty)}",
                $"{AdjustmentHash(config)}"),

            AnalysisMode.Commercial => Invariant(
                $"analysis|v1|mode={mode}|action={action}|chap={config.ChapterAnalyzerCommercialPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumCommercialDuration}|max={config.MaximumCommercialDuration}",
                $"{AdjustmentHash(config)}"),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        var hash = ComputeHash(input);
        if (mode is not (AnalysisMode.Recap or AnalysisMode.Preview))
        {
            return hash;
        }

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
            includeSubtitleSettings: false);
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

    private static string ChromaprintStreamToken(PluginConfiguration config)
        => FormattableString.Invariant(
            $"|audioLanguage={NormalizeAudioLanguage(config.PreferredAudioLanguage)}|audioMostChannels={config.PreferAudioStreamWithMostChannels}");

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
