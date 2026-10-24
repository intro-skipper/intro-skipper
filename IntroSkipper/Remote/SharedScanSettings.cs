// SPDX-FileCopyrightText: 2026 rais
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using SegmentScanner.Contracts;

namespace IntroSkipper.Remote;

/// <summary>
/// Captures detector inputs without credentials, filesystem bindings, node policy or player UI settings.
/// </summary>
internal static class SharedScanSettings
{
    /// <summary>
    /// Captures effective shared settings; host resource limits remain node-owned.
    /// </summary>
    /// <param name="configuration">Current server configuration.</param>
    /// <param name="episode">Optional effective season overrides.</param>
    /// <param name="action">Effective analyzer action.</param>
    /// <returns>The immutable portable detector settings.</returns>
    public static JobScanSettings Capture(PluginConfiguration configuration, QueuedEpisode? episode = null, AnalyzerAction action = AnalyzerAction.Default)
        => new()
        {
            Action = action.ToString(),
            Configuration = JsonSerializer.SerializeToElement(
                new
                {
                configuration.AutoDetectIntros,
                configuration.ReanalyzeSettledSeasons,
                configuration.UseSettledSeasonChromaprintConsensus,
                configuration.SettledSeasonChromaprintConsensusEpisodes,
                configuration.SettledSeasonDelayHours,
                configuration.AnalyzeSeasonZero,
                configuration.PreferChromaprint,
                configuration.EnhanceChapterCredits,
                configuration.CacheCompressionLevel,
                configuration.UseLegacyBlackFrameAnalyzer,
                configuration.RefineCreditsBoundary,
                configuration.DetectNonBlackCredits,
                configuration.ScanIntroduction,
                configuration.ScanCredits,
                configuration.ScanRecap,
                configuration.ScanPreview,
                configuration.ScanCommercial,
                AnalysisPercent = episode?.AnalysisPercentOverride ?? configuration.AnalysisPercent,
                AnalysisLengthLimit = episode?.AnalysisLengthLimitOverride ?? configuration.AnalysisLengthLimit,
                configuration.FullLengthChapters,
                configuration.UseEmbeddedChaptersForAnalysis,
                configuration.EnableSponsorBlockChapterDetection,
                configuration.SkipFirstEpisode,
                configuration.SkipFirstEpisodeAnime,
                AnimePreviewFromCreditsEnd = episode?.PreviewFromCreditsEndOverride ?? configuration.AnimePreviewFromCreditsEnd,
                EnableSubtitleRecapDetection = episode?.SubtitleRecapDetectionOverride ?? configuration.EnableSubtitleRecapDetection,
                EnableSubtitlePreviewDetection = episode?.SubtitlePreviewDetectionOverride ?? configuration.EnableSubtitlePreviewDetection,
                configuration.MinimumIntroDuration,
                configuration.MaximumIntroDuration,
                configuration.MinimumCreditsDuration,
                configuration.MaximumCreditsDuration,
                configuration.MaximumMovieCreditsDuration,
                configuration.ProbeAudioDuration,
                configuration.PreferredAudioLanguage,
                configuration.PreferAudioStreamWithMostChannels,
                configuration.MinimumRecapDuration,
                configuration.MaximumRecapDuration,
                configuration.MinimumRecapDetectionDuration,
                configuration.MaximumRecapDetectionDuration,
                configuration.DetectRecapUsingBlackFrames,
                configuration.AnchorRecapToColdOpen,
                configuration.MinimumPreviewDuration,
                configuration.MaximumPreviewDuration,
                configuration.MinimumCommercialDuration,
                configuration.MaximumCommercialDuration,
                configuration.BlackFrameMinimumPercentage,
                configuration.BlackFrameThreshold,
                configuration.UseChapterMarkersBlackFrame,
                configuration.AdjustIntroBasedOnChapters,
                configuration.AdjustIntroBasedOnSilence,
                configuration.SnapToKeyframe,
                configuration.EndSnapThreshold,
                configuration.AdjustWindowInward,
                configuration.AdjustWindowOutward,
                configuration.ChapterAnalyzerIntroductionPattern,
                configuration.ChapterAnalyzerEndCreditsPattern,
                configuration.ChapterAnalyzerPreviewPattern,
                configuration.ChapterAnalyzerRecapPattern,
                configuration.ChapterAnalyzerCommercialPattern,
                configuration.SubtitleRecapPattern,
                configuration.SubtitlePreviewPattern,
                configuration.SubtitleLanguages,
                configuration.IntroEndOffset,
                configuration.IntroStartOffset,
                configuration.IncludeIntroStartOffsetWhenSnapping,
                configuration.MaximumFingerprintPointDifferences,
                configuration.MaximumTimeSkip,
                configuration.InvertedIndexShift,
                configuration.SilenceDetectionMaximumNoise,
                configuration.SilenceDetectionMinimumDuration,
                configuration.ProcessThreads,
                configuration.ScanTimeoutSeconds,
                },
                WireJson.Options),
        };
}
