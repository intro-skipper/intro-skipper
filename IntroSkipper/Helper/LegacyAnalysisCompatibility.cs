// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.Helper;

/// <summary>
/// Adopts completed 10.11 analysis under the 12.0 baseline without running detection.
/// The legacy hash inputs are frozen to 10.11. Only hashes matching the retained
/// settings are eligible; settings introduced in 12.0 must still have their defaults.
/// Rewriting the completion record makes adoption one-time and preserves ordinary
/// hash invalidation afterwards. Missing records and unknown hashes are never adopted.
/// </summary>
internal static class LegacyAnalysisCompatibility
{
    internal static async Task<bool> UpgradeAsync(
        IIntroSkipperDatabase database,
        SeasonQueueSnapshot snapshot,
        PluginConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (ConfigHasher.NormalizeAudioLanguage(config.PreferredAudioLanguage).Length != 0
            || !config.PreferAudioStreamWithMostChannels)
        {
            return false;
        }

        var updated = false;
        foreach (var modeGroup in snapshot.AnalyzedConfigHashes.GroupBy(pair => pair.Key.Mode))
        {
            var mode = modeGroup.Key;
            if (mode is not (AnalysisMode.Introduction or AnalysisMode.Credits or AnalysisMode.Recap)
                || (mode == AnalysisMode.Credits && config.UseLegacyBlackFrameAnalyzer)
                || (mode == AnalysisMode.Recap && config.AnchorRecapToColdOpen))
            {
                continue;
            }

            var action = snapshot.AnalyzerActionByMode.GetValueOrDefault(mode, AnalyzerAction.Default);
            var upgrades = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var available in new[] { false, true })
            {
                var currentHash = ConfigHasher.Analysis(config, mode, action, available);
                upgrades[AnalysisHash(config, mode, action, available, false)] = currentHash;
                if (mode == AnalysisMode.Credits)
                {
                    upgrades[AnalysisHash(config, mode, action, available, true)] = currentHash;
                }
            }

            foreach (var hashGroup in modeGroup.GroupBy(pair => pair.Value))
            {
                if (upgrades.TryGetValue(hashGroup.Key, out var currentHash))
                {
                    updated |= await database.UpgradeAnalysisHashAsync(
                        mode,
                        hashGroup.Select(pair => pair.Key.ItemId).ToArray(),
                        hashGroup.Key,
                        currentHash,
                        cancellationToken).ConfigureAwait(false) > 0;
                }
            }
        }

        return updated;
    }

    internal static string AnalysisHash(
        PluginConfiguration config,
        AnalysisMode mode,
        AnalyzerAction action,
        bool ffmpegValid,
        bool alternativeBlackFrameAnalyzer)
    {
        var input = mode switch
        {
            AnalysisMode.Introduction => Invariant(
                $"analysis|v1|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerIntroductionPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct={config.AnalysisPercent}|limit={config.AnalysisLengthLimit}|min={config.MinimumIntroDuration}|max={config.MaximumIntroDuration}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}"),

            AnalysisMode.Credits => Invariant(
                $"analysis|v3|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerEndCreditsPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct={config.AnalysisPercent}|maxCredits={config.MaximumCreditsDuration}|maxMovie={config.MaximumMovieCreditsDuration}|probe={config.ProbeAudioDuration}",
                $"|minRegion={config.MinimumIntroDuration}",
                $"|min={config.MinimumCreditsDuration}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}|bfchap={config.UseChapterMarkersBlackFrame}",
                $"|bfalt={alternativeBlackFrameAnalyzer}|bfrefine={config.RefineCreditsBoundary}|bfaltVersion=2{(alternativeBlackFrameAnalyzer ? FormattableString.Invariant($"|nonblack={config.DetectNonBlackCredits}") : string.Empty)}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}",
                $"|animePreview={config.AnimePreviewFromCreditsEnd}"),

            AnalysisMode.Recap => Invariant(
                $"analysis|v3|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerRecapPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumRecapDuration}|max={config.MaximumRecapDuration}",
                $"|detMin={config.MinimumRecapDetectionDuration}|detMax={config.MaximumRecapDetectionDuration}",
                $"|recapBlackFrames={config.DetectRecapUsingBlackFrames}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}",
                $"|pct={config.AnalysisPercent}|limit={config.AnalysisLengthLimit}|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}"),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        input += Invariant(
            $"|chapAdjust={config.AdjustIntroBasedOnChapters}|silence={config.AdjustIntroBasedOnSilence}|keyframe={config.SnapToKeyframe}",
            $"|endSnap={config.EndSnapThreshold}|winIn={config.AdjustWindowInward}|winOut={config.AdjustWindowOutward}",
            $"|noise={config.SilenceDetectionMaximumNoise}|silDur={config.SilenceDetectionMinimumDuration}",
            $"|startOffset={config.IntroStartOffset}|includeStartOffsetWhenSnapping={config.IncludeIntroStartOffsetWhenSnapping}|endOffset={config.IntroEndOffset}");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)), 0, 8);
    }

    private static string Invariant(params FormattableString[] parts)
        => string.Concat(parts.Select(FormattableString.Invariant));
}
