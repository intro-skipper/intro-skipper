namespace ConfusedPolarBear.Plugin.IntroSkipper.Configuration;

/// <summary>
/// User interface configuration.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="UserInterfaceConfiguration"/> class.
/// </remarks>
/// <param name="visible">Skip button visibility.</param>
/// <param name="introText">Skip button intro text.</param>
/// <param name="creditsText">Skip button end credits text.</param>
public class UserInterfaceConfiguration(bool visible, string introText, string creditsText)
{
    /// <summary>
    /// Gets or sets a value indicating whether to show the skip intro button.
    /// </summary>
    public bool SkipButtonVisible { get; set; } = visible;

    /// <summary>
    /// Gets or sets the text to display in the skip intro button in introduction mode.
    /// </summary>
    public string SkipButtonIntroText { get; set; } = introText;

    /// <summary>
    /// Gets or sets the text to display in the skip intro button in end credits mode.
    /// </summary>
    public string SkipButtonEndCreditsText { get; set; } = creditsText;
}
