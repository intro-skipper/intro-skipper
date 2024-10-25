// Copyright (C) 2024 Intro-Skipper Contributors <intro-skipper.org>
// SPDX-License-Identifier: GNU General Public License v3.0 only.

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
    public UserInterfaceConfiguration(bool visible, string introText, string creditsText)
    {
        SkipButtonVisible = visible;
        SkipButtonIntroText = introText;
        SkipButtonEndCreditsText = creditsText;
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
}
