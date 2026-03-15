// SPDX-FileCopyrightText: 2019 dkanada
// SPDX-FileCopyrightText: 2019 Phallacy
// SPDX-FileCopyrightText: 2021 Cody Robibero
// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024 CasuallyFilthy
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 theMasterpc
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace IntroSkipper.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
    }

    // ===== Analysis settings =====

    /// <summary>
    /// Gets or sets the comma separated list of series names to exclude from analysis.
    /// </summary>
    public string ExcludeSeries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to automatically scan newly added items.
    /// </summary>
    public bool AutoDetectIntros { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to analyze season 0.
    /// </summary>
    public bool AnalyzeSeasonZero { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to only use chromaprint.
    /// </summary>
    public bool PreferChromaprint { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the episode's fingerprint should be cached to the filesystem.
    /// </summary>
    public bool CacheFingerprints { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to use the alternative black frame analyzer.
    /// </summary>
    public bool UseAlternativeBlackFrameAnalyzer { get; set; }

    // ===== Media Segment handling =====

    /// <summary>
    /// Gets or sets a value indicating whether to update Media Segments.
    /// </summary>
    public bool UpdateMediaSegments { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to regenerate all Media Segments during the next scan.
    /// By default, Media Segments are only written for a season if the season had at least one newly analyzed episode.
    /// If this is set, all Media Segments will be regenerated and overwrite any existing Media Segments.
    /// </summary>
    public bool RebuildMediaSegments { get; set; } = true;

    // ===== Custom analysis settings =====

    /// <summary>
    /// Gets or sets a value indicating whether Introductions should be analyzed.
    /// </summary>
    public bool ScanIntroduction { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Credits should be analyzed.
    /// </summary>
    public bool ScanCredits { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Recaps should be analyzed.
    /// </summary>
    public bool ScanRecap { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Previews should be analyzed.
    /// </summary>
    public bool ScanPreview { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Commercials should be analyzed.
    /// </summary>
    public bool ScanCommercial { get; set; } = true;

    /// <summary>
    /// Gets or sets the percentage of each episode's audio track to analyze.
    /// </summary>
    public int AnalysisPercent { get; set; } = 25;

    /// <summary>
    /// Gets or sets the upper limit (in minutes) on the length of each episode's audio track that will be analyzed.
    /// </summary>
    public int AnalysisLengthLimit { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether to use the minimum and maximum duration for chapters.
    /// </summary>
    public bool FullLengthChapters { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the introduction in the first episode of a season should be ignored.
    /// </summary>
    public bool SkipFirstEpisode { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether skipping first episode should only apply to anime.
    /// </summary>
    public bool SkipFirstEpisodeAnime { get; set; } = false;

    /// <summary>
    /// Gets or sets the minimum length of similar audio that will be considered an introduction.
    /// </summary>
    public int MinimumIntroDuration { get; set; } = 15;

    /// <summary>
    /// Gets or sets the maximum length of similar audio that will be considered an introduction.
    /// </summary>
    public int MaximumIntroDuration { get; set; } = 120;

    /// <summary>
    /// Gets or sets the minimum length of similar audio that will be considered ending credits.
    /// </summary>
    public int MinimumCreditsDuration { get; set; } = 15;

    /// <summary>
    /// Gets or sets the upper limit (in seconds) on the length of each episode's audio track that will be analyzed when searching for ending credits.
    /// </summary>
    public int MaximumCreditsDuration { get; set; } = 450;

    /// <summary>
    /// Gets or sets the upper limit (in seconds) on the length of a movie segment that will be analyzed when searching for ending credits.
    /// </summary>
    public int MaximumMovieCreditsDuration { get; set; } = 900;

    /// <summary>
    /// Gets or sets the minimum length of similar audio that will be considered a recap.
    /// </summary>
    public int MinimumRecapDuration { get; set; } = 15;

    /// <summary>
    /// Gets or sets the maximum length of similar audio that will be considered a recap.
    /// </summary>
    public int MaximumRecapDuration { get; set; } = 120;

    /// <summary>
    /// Gets or sets the minimum length of similar audio that will be considered a preview.
    /// </summary>
    public int MinimumPreviewDuration { get; set; } = 15;

    /// <summary>
    /// Gets or sets the maximum length of similar audio that will be considered a preview.
    /// </summary>
    public int MaximumPreviewDuration { get; set; } = 120;

    /// <summary>
    /// Gets or sets the minimum length of similar audio that will be considered a commercial.
    /// </summary>
    public int MinimumCommercialDuration { get; set; } = 15;

    /// <summary>
    /// Gets or sets the maximum length of similar audio that will be considered a commercial.
    /// </summary>
    public int MaximumCommercialDuration { get; set; } = 120;

    /// <summary>
    /// Gets or sets the minimum percentage of a frame that must consist of black pixels before it is considered a black frame.
    /// </summary>
    public int BlackFrameMinimumPercentage { get; set; } = 85;

    /// <summary>
    /// Gets or sets the threshold for black frame detection.
    /// </summary>
    public int BlackFrameThreshold { get; set; } = 28;

    /// <summary>
    /// Gets or sets a value indicating whether to use chapter markers for credits detection.
    /// </summary>
    public bool UseChapterMarkersBlackFrame { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to adjust segment based on chapter marks.
    /// </summary>
    public bool AdjustIntroBasedOnChapters { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to adjust the end of a segment based on silence.
    /// </summary>
    public bool AdjustIntroBasedOnSilence { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to snap the end of a segment to the nearest keyframe.
    /// </summary>
    public bool SnapToKeyframe { get; set; } = true;

    /// <summary>
    /// Gets or sets window in seconds to snap segments to the end of the episode.
    /// This gets applied at the very end.
    /// </summary>
    public double EndSnapThreshold { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the number of seconds to search toward the interior of a segment
    /// when looking for adjustment points (like chapter boundaries, silence, or keyframes).
    /// Used to narrow or tighten segment boundaries.
    /// </summary>
    public double AdjustWindowInward { get; set; } = 5.0;

    /// <summary>
    /// Gets or sets the number of seconds to search away from a segment
    /// when looking for adjustment points (like chapter boundaries, silence, or keyframes).
    /// Used to expand or widen segment boundaries.
    /// </summary>
    public double AdjustWindowOutward { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the regular expression used to detect introduction chapters.
    /// </summary>
    public string ChapterAnalyzerIntroductionPattern { get; set; } =
        @"(^|\s)(Intro|Introduction|OP|Opening)(?!\sEnd)(\s|$)";

    /// <summary>
    /// Gets or sets the regular expression used to detect ending credit chapters.
    /// </summary>
    public string ChapterAnalyzerEndCreditsPattern { get; set; } =
        @"(^|\s)(Credits?|ED|Ending|Outro)(?!\sEnd)(\s|$)";

    /// <summary>
    /// Gets or sets the regular expression used to detect Preview chapters.
    /// </summary>
    public string ChapterAnalyzerPreviewPattern { get; set; } =
        @"(^|\s)(Preview|PV|Sneak\s?Peek|Coming\s?(Up|Soon)|Next\s+(time|on|episode)|Extra|Teaser|Trailer)(?!\sEnd)(\s|:|$)";

    /// <summary>
    /// Gets or sets the regular expression used to detect Recap chapters.
    /// </summary>
    public string ChapterAnalyzerRecapPattern { get; set; } =
        @"(^|\s)(Re?cap|Sum{1,2}ary|Prev(ious(ly)?)?|(Last|Earlier)(\s\w+)?|Catch[ -]up)(?!\sEnd)(\s|:|$)";

    /// <summary>
    /// Gets or sets the regular expression used to detect Commercial chapters.
    /// </summary>
    public string ChapterAnalyzerCommercialPattern { get; set; } =
        @"(^|\s)(Ad(vert(isement)?)?|Commercial)(?!\sEnd)(\s|$)";

    // ===== Playback settings =====

    /// <summary>
    /// Gets or sets the amount of intro to play (in seconds).
    /// </summary>
    public int IntroEndOffset { get; set; }

    /// <summary>
    /// Gets or sets the amount of intro at start to play (in seconds).
    /// </summary>
    public int IntroStartOffset { get; set; }

    // ===== Internal algorithm settings =====

    /// <summary>
    /// Gets or sets the maximum number of bits (out of 32 total) that can be different between two Chromaprint points before they are considered dissimilar.
    /// Defaults to 6 (81% similar).
    /// </summary>
    public int MaximumFingerprintPointDifferences { get; set; } = 6;

    /// <summary>
    /// Gets or sets the maximum number of seconds that can pass between two similar fingerprint points before a new time range is started.
    /// </summary>
    public double MaximumTimeSkip { get; set; } = 3.5;

    /// <summary>
    /// Gets or sets the amount to shift inverted indexes by.
    /// </summary>
    public int InvertedIndexShift { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum amount of noise (in dB) that is considered silent.
    /// Lowering this number will increase the filter's sensitivity to noise.
    /// </summary>
    public int SilenceDetectionMaximumNoise { get; set; } = -50;

    /// <summary>
    /// Gets or sets the minimum duration of audio (in seconds) that is considered silent.
    /// </summary>
    public double SilenceDetectionMinimumDuration { get; set; } = 0.33;

    // ===== Localization support =====

    /// <summary>
    /// Gets or sets the max degree of parallelism used when analyzing episodes.
    /// </summary>
    public int MaxParallelism { get; set; } = 2;

    /// <summary>
    /// Gets or sets the number of threads for a ffmpeg process.
    /// </summary>
    public int ProcessThreads { get; set; }

    /// <summary>
    /// Gets or sets the relative priority for a ffmpeg process.
    /// </summary>
    public ProcessPriorityClass ProcessPriority { get; set; } = ProcessPriorityClass.BelowNormal;

    /// <summary>
    /// Gets or sets a value indicating whether to use the File Transformation plugin if available.
    /// </summary>
    public bool UseFileTransformationPlugin { get; set; }

    /// <summary>
    /// Gets or sets the amount of seconds to wait before hiding the skip button.
    /// </summary>
    public int SkipbuttonHideDelay { get; set; } = 8;

    /// <summary>
    /// Gets or sets a value indicating whether to enable the main menu entry for the plugin.
    /// </summary>
    public bool EnableMainMenu { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the File Transformation plugin is enabled.
    /// This value is set by the Plugin during initialization.
    /// </summary>
    [XmlIgnore]
    public bool FileTransformationPluginEnabled { get; set; }
}
