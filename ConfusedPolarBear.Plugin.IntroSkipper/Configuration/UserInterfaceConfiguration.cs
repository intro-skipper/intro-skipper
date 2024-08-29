namespace ConfusedPolarBear.Plugin.IntroSkipper.Configuration;

/// <summary>
/// User interface configuration.
/// </summary>
public class UserInterfaceConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UserInterfaceConfiguration"/> class.
    /// </summary>
    /// <param name="visible">Skip button visibility.</param>
    /// <param name="introText">Skip button intro text.</param>
    /// <param name="creditsText">Skip button end credits text.</param>
    /// <param name="autoSkipPipMode">Auto skip picture-in-picture.</param>
    public UserInterfaceConfiguration(bool visible, string introText, string creditsText, bool autoSkipPipMode)
    {
        SkipButtonVisible = visible;
        SkipButtonIntroText = introText;
        SkipButtonEndCreditsText = creditsText;
        AutoSkipPip = autoSkipPipMode;
    }

    /// <summary>
    /// Gets or sets a value indicating whether to show the skip intro button.
    /// </summary>
    public bool SkipButtonVisible { get; set; }

    /// <summary>
    /// Gets or sets the text to display in the skip intro button in introduction mode.
    /// </summary>
    public string SkipButtonIntroText { get; set; }

    /// <summary>
    /// Gets or sets the text to display in the skip intro button in end credits mode.
    /// </summary>
    public string SkipButtonEndCreditsText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to automatically skip the intro when in picture-in-picture mode.
    /// </summary>
    public bool AutoSkipPip { get; set; }
}
