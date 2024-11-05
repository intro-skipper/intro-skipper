// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace IntroSkipper.Configuration;

/// <summary>
/// User interface configuration.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="UserInterfaceConfiguration"/> class.
/// </remarks>
/// <param name="visible">Skip button visibility.</param>
/// <param name="introText">Skip button intro text.</param>
/// <param name="creditsText">Skip button end credits text.</param>
/// <param name="recapText">Skip button recap text.</param>
/// <param name="previewText">Skip button preview text.</param>
/// <param name="autoSkip">Auto Skip Intro.</param>
/// <param name="autoSkipCredits">Auto Skip Credits.</param>
/// <param name="autoSkipRecap">Auto Skip Recap.</param>
/// <param name="autoSkipPreview">Auto Skip Preview.</param>
/// <param name="clientList">Auto Skip Clients.</param>
public class UserInterfaceConfiguration(bool visible, string introText, string creditsText, string recapText, string previewText, bool autoSkip, bool autoSkipCredits, bool autoSkipRecap, bool autoSkipPreview, string clientList)
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

    /// <summary>
    /// Gets or sets the text to display in the skip recap button.
    /// </summary>
    public string SkipButtonRecapText { get; set; } = recapText;

    /// <summary>
    /// Gets or sets the text to display in the skip preview button.
    /// </summary>
    public string SkipButtonPreviewText { get; set; } = previewText;

    /// <summary>
    /// Gets or sets a value indicating whether auto skip intro.
    /// </summary>
    public bool AutoSkip { get; set; } = autoSkip;

    /// <summary>
    /// Gets or sets a value indicating whether auto skip credits.
    /// </summary>
    public bool AutoSkipCredits { get; set; } = autoSkipCredits;

    /// <summary>
    /// Gets or sets a value indicating whether auto skip recap.
    /// </summary>
    public bool AutoSkipRecap { get; set; } = autoSkipRecap;

    /// <summary>
    /// Gets or sets a value indicating whether auto skip preview.
    /// </summary>
    public bool AutoSkipPreview { get; set; } = autoSkipPreview;

    /// <summary>
    /// Gets or sets a value indicating clients to auto skip for.
    /// </summary>
    public string ClientList { get; set; } = clientList;
}
