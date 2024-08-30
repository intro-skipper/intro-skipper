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
    /// <param name="clientConfiguration">Client configuration visibility.</param>
    /// <param name="defaultsOptionSupport">Defaults option for clients that support picture-in-picture.</param>
    /// <param name="defaultsOptionUnSupport">Defaults option for clients that do not support picture-in-picture.</param>
    public UserInterfaceConfiguration(bool visible, string introText, string creditsText, bool clientConfiguration, string defaultsOptionSupport, string defaultsOptionUnSupport)
    {
        SkipButtonVisible = visible;
        SkipButtonIntroText = introText;
        SkipButtonEndCreditsText = creditsText;
        ClientConfiguration = clientConfiguration;
        DefaultsOptionSupport = defaultsOptionSupport;
        DefaultsOptionUnSupport = defaultsOptionUnSupport;
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
    /// Gets or sets a value indicating whether to show the intro skip configuration in the player.
    /// </summary>
    public bool ClientConfiguration { get; set; }

    /// <summary>
    /// Gets or sets the defaults option for clients that support the picture-in-picture mode.
    /// </summary>
    public string DefaultsOptionSupport { get; set; }

    /// <summary>
    /// Gets or sets the defaults option for clients that do not support the picture-in-picture mode.
    /// </summary>
    public string DefaultsOptionUnSupport { get; set; }
}
