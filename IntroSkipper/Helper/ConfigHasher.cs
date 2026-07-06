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
public static class ConfigHasher
{
    /// <summary>
    /// Computes a hash for a stored analysis result.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer priority/action used for the season.</param>
    /// <param name="ffmpegValid">Whether the current FFmpeg build supports Chromaprint. Folded into the
    /// hash for Chromaprint-capable modes so a settled <see cref="EpisodeState.NoSegments"/> season is
    /// re-analyzed once when Chromaprint becomes available instead of being skipped forever.</param>
    /// <returns>A compact hex hash.</returns>
    public static string Analysis(PluginConfiguration config, AnalysisMode mode, AnalyzerAction action, bool ffmpegValid)
    {
        ArgumentNullException.ThrowIfNull(config);

        var input = mode switch
        {
            AnalysisMode.Introduction => Invariant(
                $"analysis|v1|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerIntroductionPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct={config.AnalysisPercent}|limit={config.AnalysisLengthLimit}|min={config.MinimumIntroDuration}|max={config.MaximumIntroDuration}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}",
                $"{AdjustmentHash(config)}"),

            AnalysisMode.Credits => Invariant(
                $"analysis|v1|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerEndCreditsPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct={config.AnalysisPercent}|maxCredits={config.MaximumCreditsDuration}|maxMovie={config.MaximumMovieCreditsDuration}|probe={config.ProbeAudioDuration}",
                $"|min={config.MinimumCreditsDuration}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}|bfchap={config.UseChapterMarkersBlackFrame}",
                $"|bfalt={config.UseAlternativeBlackFrameAnalyzer}|bfrefine={config.RefineCreditsBoundary}|bfaltVersion=2{CreditsNonBlackToken(config)}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}",
                $"|animePreview={config.AnimePreviewFromCreditsEnd}",
                $"{AdjustmentHash(config)}"),

            AnalysisMode.Recap => Invariant(
                $"analysis|v2|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerRecapPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumRecapDuration}|max={config.MaximumRecapDuration}",
                $"|detMin={config.MinimumRecapDetectionDuration}|detMax={config.MaximumRecapDetectionDuration}",
                $"|recapBlackFrames={config.DetectRecapUsingBlackFrames}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}",
                $"|pct={config.AnalysisPercent}|limit={config.AnalysisLengthLimit}|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}",
                $"{AdjustmentHash(config)}"),

            AnalysisMode.Preview => Invariant(
                $"analysis|v1|mode={mode}|action={action}|chap={config.ChapterAnalyzerPreviewPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumPreviewDuration}|max={config.MaximumPreviewDuration}",
                $"{AdjustmentHash(config)}"),

            AnalysisMode.Commercial => Invariant(
                $"analysis|v1|mode={mode}|action={action}|chap={config.ChapterAnalyzerCommercialPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumCommercialDuration}|max={config.MaximumCommercialDuration}",
                $"{AdjustmentHash(config)}"),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        return ComputeHash(input);
    }

    /// <summary>
    /// Computes a hash for FFmpeg detection cache rows.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="type">Cache entry type.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>A compact hex hash.</returns>
    public static string DetectionCache(PluginConfiguration config, CacheEntryType type, AnalysisMode mode)
    {
        ArgumentNullException.ThrowIfNull(config);

        var input = type switch
        {
            CacheEntryType.Chromaprint => Invariant(
                $"cache|v1|{type}|{mode}|pct={config.AnalysisPercent}|limit={config.AnalysisLengthLimit}|maxCredits={config.MaximumCreditsDuration}|maxMovie={config.MaximumMovieCreditsDuration}|probe={config.ProbeAudioDuration}"),

            CacheEntryType.Silence => Invariant(
                $"cache|v1|{type}|noise={config.SilenceDetectionMaximumNoise}|dur={config.SilenceDetectionMinimumDuration}"),

            CacheEntryType.BlackFrame => Invariant(
                $"cache|v1|{type}|{mode}|threshold={config.BlackFrameThreshold}"),

            CacheEntryType.BlackInterval => Invariant(
                $"cache|v1|{type}|{mode}|blackdetect=v1|threshold={config.BlackFrameThreshold}|bfmin={config.BlackFrameMinimumPercentage}|duration={BlackInterval.MinimumDetectionDuration}"),

            CacheEntryType.Keyframe => $"cache|v1|{type}",

            CacheEntryType.KeyframeVisual => $"cache|v1|{type}|{mode}",

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

        return ComputeHash(input);
    }

    // DetectNonBlackCredits only affects output when the alternative analyzer is active; including it
    // unconditionally would invalidate cached credits on the default BlackFrameAnalyzer path, which
    // cannot observe the setting (the UI also hides it there).
    private static string CreditsNonBlackToken(PluginConfiguration config)
        => config.UseAlternativeBlackFrameAnalyzer
            ? FormattableString.Invariant($"|nonblack={config.DetectNonBlackCredits}")
            : string.Empty;

    private static string AdjustmentHash(PluginConfiguration config)
        => Invariant(
            $"|chapAdjust={config.AdjustIntroBasedOnChapters}|silence={config.AdjustIntroBasedOnSilence}|keyframe={config.SnapToKeyframe}",
            $"|endSnap={config.EndSnapThreshold}|winIn={config.AdjustWindowInward}|winOut={config.AdjustWindowOutward}",
            $"|noise={config.SilenceDetectionMaximumNoise}|silDur={config.SilenceDetectionMinimumDuration}",
            $"|startOffset={config.IntroStartOffset}|endOffset={config.IntroEndOffset}");

    private static string Invariant(params FormattableString[] parts)
        => string.Concat(parts.Select(FormattableString.Invariant));

    private static string ComputeHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 8);
    }
}
