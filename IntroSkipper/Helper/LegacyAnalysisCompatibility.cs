// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;

namespace IntroSkipper.Helper;

/// <summary>
/// Adopts completed 10.11.22–24 analysis under the 12.0 baseline without running detection.
/// The legacy hash inputs are frozen to those releases. Only hashes matching the retained
/// settings are eligible; settings introduced in 12.0 must still have their defaults. For
/// credits seasons pinned to one analyzer, settings belonging to the other analyzer are ignored
/// only when that analyzer is actually exclusive; an unavailable Chromaprint action uses the
/// default chapter-first policy instead.
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
        var updated = false;
        foreach (var modeGroup in snapshot.AnalysisRecords.GroupBy(pair => pair.Key.Mode))
        {
            var mode = modeGroup.Key;
            var action = snapshot.AnalyzerActionByMode.GetValueOrDefault(mode, AnalyzerAction.Default);
            if (!AnalysisHelpers.IsSupported(mode)
                || (mode == AnalysisMode.Recap && config.AnchorRecapToColdOpen))
            {
                continue;
            }

            var hasMultipleItems = modeGroup.Count() > 1;
            var upgrades = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var available in new[] { false, true })
            {
                var creditsChromaprintAvailable = available && hasMultipleItems;
                var chromaprintExclusive = mode == AnalysisMode.Credits
                    && action == AnalyzerAction.Chromaprint
                    && creditsChromaprintAvailable;
                var usesChromaprint = mode is AnalysisMode.Introduction or AnalysisMode.Recap
                    || (mode == AnalysisMode.Credits
                        && creditsChromaprintAvailable
                        && action is not (AnalyzerAction.None or AnalyzerAction.BlackFrame));
                var creditsUsesChapter = mode == AnalysisMode.Credits
                    && (action is AnalyzerAction.Default or AnalyzerAction.Chapter
                        || (action == AnalyzerAction.Chromaprint && !creditsChromaprintAvailable));
                var creditsUsesBlackFrame = mode == AnalysisMode.Credits
                    && action is not AnalyzerAction.None
                    && (action is not AnalyzerAction.Chromaprint || !creditsChromaprintAvailable);
                if ((usesChromaprint && (ConfigHasher.NormalizeAudioLanguage(config.PreferredAudioLanguage).Length != 0
                        || !config.PreferAudioStreamWithMostChannels))
                    || (creditsUsesBlackFrame && config.UseLegacyBlackFrameAnalyzer)
                    || (creditsUsesChapter && config.EnhanceChapterCredits))
                {
                    continue;
                }

                var currentHash = ConfigHasher.Analysis(config, mode, action, available);
                foreach (var release in new[] { 22, 23, 24 })
                {
                    if (release < 24 && (config.IncludeIntroStartOffsetWhenSnapping
                        || mode is AnalysisMode.Credits or AnalysisMode.Preview))
                    {
                        continue;
                    }

                    foreach (var normalizeInactiveSettings in new[] { false, true })
                    {
                        if (normalizeInactiveSettings
                            && mode == AnalysisMode.Credits
                            && action == AnalyzerAction.Chromaprint
                            && available
                            && !chromaprintExclusive)
                        {
                            continue;
                        }

                        upgrades[AnalysisHash(config, mode, action, available, false, release, normalizeInactiveSettings)] = currentHash;
                        if (mode == AnalysisMode.Credits)
                        {
                            upgrades[AnalysisHash(config, mode, action, available, true, release, normalizeInactiveSettings)] = currentHash;
                        }
                    }
                }
            }

            foreach (var hashGroup in modeGroup.GroupBy(pair => pair.Value.ConfigHash))
            {
                if (upgrades.TryGetValue(hashGroup.Key, out var currentHash) && hashGroup.Key != currentHash)
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
        bool alternativeBlackFrameAnalyzer,
        int release = 24,
        bool ignoreInactiveCreditsSettings = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(release, 22);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(release, 24);
        var defaults = ignoreInactiveCreditsSettings ? new PluginConfiguration() : config;
        var creditsUsesChapter = action is AnalyzerAction.Default or AnalyzerAction.Chapter
            || (action == AnalyzerAction.Chromaprint && !ffmpegValid);
        var creditsUsesChromaprint = ffmpegValid
            && action is not (AnalyzerAction.None or AnalyzerAction.BlackFrame);
        var creditsUsesBlackFrame = action is not AnalyzerAction.None
            && (action is not AnalyzerAction.Chromaprint || !ffmpegValid);
        var input = mode switch
        {
            AnalysisMode.Introduction => Invariant(
                $"analysis|v1|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerIntroductionPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}",
                $"|pct={config.AnalysisPercent}|limit={config.AnalysisLengthLimit}|min={config.MinimumIntroDuration}|max={config.MaximumIntroDuration}",
                $"|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}"),

            AnalysisMode.Credits => Invariant(
                $"analysis|v{release - 21}|mode={mode}|action={action}|prefer={(creditsUsesChapter ? config.PreferChromaprint : defaults.PreferChromaprint)}|chap={(creditsUsesChapter ? config.ChapterAnalyzerEndCreditsPattern : defaults.ChapterAnalyzerEndCreditsPattern)}|fullchap={(creditsUsesChapter ? config.FullLengthChapters : defaults.FullLengthChapters)}|sbchap={(creditsUsesChapter ? config.EnableSponsorBlockChapterDetection : defaults.EnableSponsorBlockChapterDetection)}",
                $"|pct={config.AnalysisPercent}|maxCredits={config.MaximumCreditsDuration}|maxMovie={config.MaximumMovieCreditsDuration}|probe={config.ProbeAudioDuration}",
                $"{(release == 24 ? FormattableString.Invariant($"|minRegion={(creditsUsesChromaprint ? config.MinimumIntroDuration : defaults.MinimumIntroDuration)}") : string.Empty)}",
                $"|min={config.MinimumCreditsDuration}|bfmin={(creditsUsesBlackFrame ? config.BlackFrameMinimumPercentage : defaults.BlackFrameMinimumPercentage)}|bfthr={(creditsUsesBlackFrame ? config.BlackFrameThreshold : defaults.BlackFrameThreshold)}|bfchap={(creditsUsesBlackFrame ? config.UseChapterMarkersBlackFrame : defaults.UseChapterMarkersBlackFrame)}",
                $"|bfalt={alternativeBlackFrameAnalyzer}|bfrefine={(creditsUsesBlackFrame ? config.RefineCreditsBoundary : defaults.RefineCreditsBoundary)}|bfaltVersion=2{(alternativeBlackFrameAnalyzer && creditsUsesBlackFrame ? FormattableString.Invariant($"|nonblack={config.DetectNonBlackCredits}") : string.Empty)}",
                $"|fpbits={(creditsUsesChromaprint ? config.MaximumFingerprintPointDifferences : defaults.MaximumFingerprintPointDifferences)}|skip={(creditsUsesChromaprint ? config.MaximumTimeSkip : defaults.MaximumTimeSkip)}|shift={(creditsUsesChromaprint ? config.InvertedIndexShift : defaults.InvertedIndexShift)}|chromaprint={ffmpegValid}",
                $"|animePreview={config.AnimePreviewFromCreditsEnd}"),

            AnalysisMode.Recap => Invariant(
                $"analysis|v{(release == 22 ? 1 : 3)}|mode={mode}|action={action}|prefer={config.PreferChromaprint}|chap={config.ChapterAnalyzerRecapPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumRecapDuration}|max={config.MaximumRecapDuration}",
                $"|detMin={config.MinimumRecapDetectionDuration}|detMax={config.MaximumRecapDetectionDuration}",
                $"|recapBlackFrames={config.DetectRecapUsingBlackFrames}|bfmin={config.BlackFrameMinimumPercentage}|bfthr={config.BlackFrameThreshold}",
                $"|pct={config.AnalysisPercent}|limit={config.AnalysisLengthLimit}|fpbits={config.MaximumFingerprintPointDifferences}|skip={config.MaximumTimeSkip}|shift={config.InvertedIndexShift}|chromaprint={ffmpegValid}"),

            AnalysisMode.Preview => Invariant(
                $"analysis|v{(release == 24 ? 2 : 1)}|mode={mode}|action={action}|chap={config.ChapterAnalyzerPreviewPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumPreviewDuration}|max={config.MaximumPreviewDuration}",
                $"{(release == 24 ? FormattableString.Invariant($"|animePreview={config.AnimePreviewFromCreditsEnd}") : string.Empty)}"),

            AnalysisMode.Commercial => Invariant(
                $"analysis|v1|mode={mode}|action={action}|chap={config.ChapterAnalyzerCommercialPattern}|fullchap={config.FullLengthChapters}|sbchap={config.EnableSponsorBlockChapterDetection}|min={config.MinimumCommercialDuration}|max={config.MaximumCommercialDuration}"),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        input += Invariant(
            $"|chapAdjust={config.AdjustIntroBasedOnChapters}|silence={config.AdjustIntroBasedOnSilence}|keyframe={config.SnapToKeyframe}",
            $"|endSnap={config.EndSnapThreshold}|winIn={config.AdjustWindowInward}|winOut={config.AdjustWindowOutward}",
            $"|noise={config.SilenceDetectionMaximumNoise}|silDur={config.SilenceDetectionMinimumDuration}",
            $"|startOffset={config.IntroStartOffset}{(release == 24 ? FormattableString.Invariant($"|includeStartOffsetWhenSnapping={config.IncludeIntroStartOffsetWhenSnapping}") : string.Empty)}|endOffset={config.IntroEndOffset}");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)), 0, 8);
    }

    private static string Invariant(params FormattableString[] parts)
        => string.Concat(parts.Select(FormattableString.Invariant));
}
