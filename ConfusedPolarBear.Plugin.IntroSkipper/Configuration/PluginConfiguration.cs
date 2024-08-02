using System.Collections.Generic;
using System.Diagnostics;
using MediaBrowser.Model.Plugins;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Configuration;

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
    /// Gets or sets the max degree of parallelism used when analyzing episodes.
    /// </summary>
    public int MaxParallelism { get; set; } = 2;

    /// <summary>
    /// Gets or sets the comma separated list of library names to analyze. If empty, all libraries will be analyzed.
    /// </summary>
    public string SelectedLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to scan for intros during a scheduled task.
    /// </summary>
    public bool AutoDetectIntros { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to scan for credits during a scheduled task.
    /// </summary>
    public bool AutoDetectCredits { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to analyze season 0.
    /// </summary>
    public bool AnalyzeSeasonZero { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the episode's fingerprint should be cached to the filesystem.
    /// </summary>
    public bool CacheFingerprints { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether analysis will use Chromaprint to determine fingerprints.
    /// </summary>
    public bool UseChromaprint { get; set; } = true;

    // ===== EDL handling =====

    /// <summary>
    /// Gets or sets a value indicating the action to write to created EDL files.
    /// </summary>
    public EdlAction EdlAction { get; set; } = EdlAction.None;

    /// <summary>
    /// Gets or sets a value indicating whether to regenerate all EDL files during the next scan.
    /// By default, EDL files are only written for a season if the season had at least one newly analyzed episode.
    /// If this is set, all EDL files will be regenerated and overwrite any existing EDL file.
    /// </summary>
    public bool RegenerateEdlFiles { get; set; } = false;

    // ===== Custom analysis settings =====

    /// <summary>
    /// Gets or sets the percentage of each episode's audio track to analyze.
    /// </summary>
    public int AnalysisPercent { get; set; } = 25;

    /// <summary>
    /// Gets or sets the upper limit (in minutes) on the length of each episode's audio track that will be analyzed.
    /// </summary>
    public int AnalysisLengthLimit { get; set; } = 10;

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
    public int MaximumCreditsDuration { get; set; } = 300;

    /// <summary>
    /// Gets or sets the minimum percentage of a frame that must consist of black pixels before it is considered a black frame.
    /// </summary>
    public int BlackFrameMinimumPercentage { get; set; } = 85;

    /// <summary>
    /// Gets or sets the regular expression used to detect introduction chapters.
    /// </summary>
    public string ChapterAnalyzerIntroductionPattern { get; set; } =
        @"(^|\s)(Intro|Introduction|OP|Opening)(\s|$)";

    /// <summary>
    /// Gets or sets the regular expression used to detect ending credit chapters.
    /// </summary>
    public string ChapterAnalyzerEndCreditsPattern { get; set; } =
        @"(^|\s)(Credits?|ED|Ending)(\s|$)";

    // ===== Playback settings =====

    /// <summary>
    /// Gets or sets a value indicating whether to show the skip intro button.
    /// </summary>
    public bool SkipButtonVisible { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether introductions should be automatically skipped.
    /// </summary>
    public bool AutoSkip { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether credits should be automatically skipped.
    /// </summary>
    public bool AutoSkipCredits { get; set; }

    /// <summary>
    /// Gets or sets the seconds before the intro starts to show the skip prompt at.
    /// </summary>
    public int ShowPromptAdjustment { get; set; } = 5;

    /// <summary>
    /// Gets or sets the seconds after the intro starts to hide the skip prompt at.
    /// </summary>
    public int HidePromptAdjustment { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether the introduction in the first episode of a season should be ignored.
    /// </summary>
    public bool SkipFirstEpisode { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the skip button should be displayed for the duration of the intro.
    /// </summary>
    public bool PersistSkipButton { get; set; } = true;

    /// <summary>
    /// Gets or sets the amount of intro to play (in seconds).
    /// </summary>
    public int SecondsOfIntroToPlay { get; set; } = 2;

    /// <summary>
    /// Gets or sets the amount of credits to play (in seconds).
    /// </summary>
    public int SecondsOfCreditsToPlay { get; set; } = 0;

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
    /// Gets or sets the text to display in the skip button in introduction mode.
    /// </summary>
    public string SkipButtonIntroText { get; set; } = "Skip Intro";

    /// <summary>
    /// Gets or sets the text to display in the skip button in end credits mode.
    /// </summary>
    public string SkipButtonEndCreditsText { get; set; } = "Next";

    /// <summary>
    /// Gets or sets the notification text sent after automatically skipping an introduction.
    /// </summary>
    public string AutoSkipNotificationText { get; set; } = "Intro skipped";

    /// <summary>
    /// Gets or sets the notification text sent after automatically skipping credits.
    /// </summary>
    public string AutoSkipCreditsNotificationText { get; set; } = "Credits skipped";

    /// <summary>
    /// Gets or sets the number of threads for an ffmpeg process.
    /// </summary>
    public int ProcessThreads { get; set; } = 0;

    /// <summary>
    /// Gets or sets the relative priority for an ffmpeg process.
    /// </summary>
    public ProcessPriorityClass ProcessPriority { get; set; } = ProcessPriorityClass.BelowNormal;
}
