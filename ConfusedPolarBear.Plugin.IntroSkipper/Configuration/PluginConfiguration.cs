using System.Diagnostics;
using ConfusedPolarBear.Plugin.IntroSkipper.Data;
using MediaBrowser.Model.Plugins;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Configuration;

/// <summary>
/// Represents the configuration for the IntroSkipper plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Default list of clients.
    /// </summary>
    private const string DefaultClientList = "Android TV, Kodi";

    /// <summary>
    /// Default pattern for detecting intros.
    /// </summary>
    private const string DefaultIntroPattern = @"(^|\s)(Intro|Introduction|OP|Opening)(\s|$)";

    /// <summary>
    /// Default pattern for detecting credits.
    /// </summary>
    private const string DefaultCreditsPattern = @"(^|\s)(Credits?|ED|Ending|End|Outro)(\s|$)";

    /// <summary>
    /// Backing field for SelectAllLibraries property.
    /// </summary>
    private bool? _selectAllLibraries;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        // Initialize default values
        MaxParallelism = 2;
        ClientList = DefaultClientList;
        AnalysisPercent = 25;
        AnalysisLengthLimit = 10;
        MinimumIntroDuration = 15;
        MaximumIntroDuration = 120;
        MinimumCreditsDuration = 15;
        MaximumCreditsDuration = 300;
        BlackFrameMinimumPercentage = 85;
        ChapterAnalyzerIntroductionPattern = DefaultIntroPattern;
        ChapterAnalyzerEndCreditsPattern = DefaultCreditsPattern;
        SkipButtonVisible = true;
        ShowPromptAdjustment = 5;
        HidePromptAdjustment = 10;
        SkipFirstEpisode = true;
        PersistSkipButton = true;
        RemainingSecondsOfIntro = 2;
        MaximumFingerprintPointDifferences = 6;
        MaximumTimeSkip = 3.5;
        InvertedIndexShift = 2;
        SilenceDetectionMaximumNoise = -50;
        SilenceDetectionMinimumDuration = 0.33;
        SkipButtonIntroText = "Skip Intro";
        SkipButtonEndCreditsText = "Next";
        AutoSkipNotificationText = "Intro skipped";
        AutoSkipCreditsNotificationText = "Credits skipped";
        ProcessPriority = ProcessPriorityClass.BelowNormal;
    }

    // Analysis settings

    /// <summary>
    /// Gets or sets the maximum number of parallel operations.
    /// </summary>
    public int MaxParallelism { get; set; }

    /// <summary>
    /// Gets or sets the selected libraries.
    /// </summary>
    public string SelectedLibraries { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether all libraries are selected.
    /// </summary>
    public bool SelectAllLibraries
    {
        get => _selectAllLibraries ?? string.IsNullOrEmpty(SelectedLibraries);
        set => _selectAllLibraries = value;
    }

    /// <summary>
    /// Gets or sets the list of clients.
    /// </summary>
    public string ClientList { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to auto-detect intros.
    /// </summary>
    public bool AutoDetectIntros { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to auto-detect credits.
    /// </summary>
    public bool AutoDetectCredits { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to analyze season zero.
    /// </summary>
    public bool AnalyzeSeasonZero { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to cache fingerprints.
    /// </summary>
    public bool CacheFingerprints { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to use Chromaprint.
    /// </summary>
    public bool UseChromaprint { get; set; } = true;

    // EDL handling

    /// <summary>
    /// Gets or sets the EDL action.
    /// </summary>
    public EdlAction EdlAction { get; set; } = EdlAction.None;

    /// <summary>
    /// Gets or sets a value indicating whether to regenerate EDL files.
    /// </summary>
    public bool RegenerateEdlFiles { get; set; }

    // Custom analysis settings

    /// <summary>
    /// Gets or sets the analysis percent.
    /// </summary>
    public int AnalysisPercent { get; set; }

    /// <summary>
    /// Gets or sets the analysis length limit.
    /// </summary>
    public int AnalysisLengthLimit { get; set; }

    /// <summary>
    /// Gets or sets the minimum intro duration.
    /// </summary>
    public int MinimumIntroDuration { get; set; }

    /// <summary>
    /// Gets or sets the maximum intro duration.
    /// </summary>
    public int MaximumIntroDuration { get; set; }

    /// <summary>
    /// Gets or sets the minimum credits duration.
    /// </summary>
    public int MinimumCreditsDuration { get; set; }

    /// <summary>
    /// Gets or sets the maximum credits duration.
    /// </summary>
    public int MaximumCreditsDuration { get; set; }

    /// <summary>
    /// Gets or sets the minimum percentage for black frame detection.
    /// </summary>
    public int BlackFrameMinimumPercentage { get; set; }

    /// <summary>
    /// Gets or sets the pattern for detecting introductions in chapter analysis.
    /// </summary>
    public string ChapterAnalyzerIntroductionPattern { get; set; }

    /// <summary>
    /// Gets or sets the pattern for detecting end credits in chapter analysis.
    /// </summary>
    public string ChapterAnalyzerEndCreditsPattern { get; set; }

    // Playback settings

    /// <summary>
    /// Gets or sets a value indicating whether the skip button is visible.
    /// </summary>
    public bool SkipButtonVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to auto-skip.
    /// </summary>
    public bool AutoSkip { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to auto-skip credits.
    /// </summary>
    public bool AutoSkipCredits { get; set; }

    /// <summary>
    /// Gets or sets the adjustment for showing the prompt.
    /// </summary>
    public int ShowPromptAdjustment { get; set; }

    /// <summary>
    /// Gets or sets the adjustment for hiding the prompt.
    /// </summary>
    public int HidePromptAdjustment { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to skip the first episode.
    /// </summary>
    public bool SkipFirstEpisode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to persist the skip button.
    /// </summary>
    public bool PersistSkipButton { get; set; }

    /// <summary>
    /// Gets or sets the remaining seconds of intro to play.
    /// </summary>
    public int RemainingSecondsOfIntro { get; set; }

    /// <summary>
    /// Gets or sets the seconds of intro start to play.
    /// </summary>
    public int SecondsOfIntroStartToPlay { get; set; }

    /// <summary>
    /// Gets or sets the seconds of credits start to play.
    /// </summary>
    public int SecondsOfCreditsStartToPlay { get; set; }

    // Internal algorithm settings

    /// <summary>
    /// Gets or sets the maximum fingerprint point differences.
    /// </summary>
    public int MaximumFingerprintPointDifferences { get; set; }

    /// <summary>
    /// Gets or sets the maximum time skip.
    /// </summary>
    public double MaximumTimeSkip { get; set; }

    /// <summary>
    /// Gets or sets the inverted index shift.
    /// </summary>
    public int InvertedIndexShift { get; set; }

    /// <summary>
    /// Gets or sets the maximum noise for silence detection.
    /// </summary>
    public int SilenceDetectionMaximumNoise { get; set; }

    /// <summary>
    /// Gets or sets the minimum duration for silence detection.
    /// </summary>
    public double SilenceDetectionMinimumDuration { get; set; }

    // Localization support

    /// <summary>
    /// Gets or sets the text for the skip intro button.
    /// </summary>
    public string SkipButtonIntroText { get; set; }

    /// <summary>
    /// Gets or sets the text for the skip end credits button.
    /// </summary>
    public string SkipButtonEndCreditsText { get; set; }

    /// <summary>
    /// Gets or sets the text for the auto-skip intro notification.
    /// </summary>
    public string AutoSkipNotificationText { get; set; }

    /// <summary>
    /// Gets or sets the text for the auto-skip credits notification.
    /// </summary>
    public string AutoSkipCreditsNotificationText { get; set; }

    // Process settings

    /// <summary>
    /// Gets or sets the number of process threads.
    /// </summary>
    public int ProcessThreads { get; set; }

    /// <summary>
    /// Gets or sets the process priority.
    /// </summary>
    public ProcessPriorityClass ProcessPriority { get; set; }
}
