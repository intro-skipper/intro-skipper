// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace IntroSkipper.Configuration;

/// <summary>
/// User interface configuration.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="UserInterfaceConfiguration"/> class.
/// </remarks>
/// <param name="autoSkip">Auto Skip Intro.</param>
/// <param name="autoSkipCredits">Auto Skip Credits.</param>
/// <param name="autoSkipRecap">Auto Skip Recap.</param>
/// <param name="autoSkipPreview">Auto Skip Preview.</param>
/// <param name="clientList">Auto Skip Clients.</param>
public class UserInterfaceConfiguration(bool autoSkip, bool autoSkipCredits, bool autoSkipRecap, bool autoSkipPreview, string clientList)
{
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
