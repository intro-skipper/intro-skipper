// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Runtime.Serialization;

namespace IntroSkipper.Data;

/// <summary>
/// Represents an item to ignore.
/// </summary>
[DataContract(Namespace = "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper")]
public class IgnoreListItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IgnoreListItem"/> class.
    /// </summary>
    public IgnoreListItem()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IgnoreListItem"/> class.
    /// </summary>
    /// <param name="seasonId">The season id.</param>
    public IgnoreListItem(Guid seasonId)
    {
        SeasonId = seasonId;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IgnoreListItem"/> class.
    /// </summary>
    /// <param name="item">The item to copy.</param>
    public IgnoreListItem(IgnoreListItem item)
    {
        SeasonId = item.SeasonId;
        IgnoreIntro = item.IgnoreIntro;
        IgnoreCredits = item.IgnoreCredits;
    }

    /// <summary>
    /// Gets or sets the season id.
    /// </summary>
    [DataMember]
    public Guid SeasonId { get; set; } = Guid.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to ignore the intro.
    /// </summary>
    [DataMember]
    public bool IgnoreIntro { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to ignore the credits.
    /// </summary>
    [DataMember]
    public bool IgnoreCredits { get; set; }

    /// <summary>
    /// Toggles the provided mode to the provided value.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="value">Value to set.</param>
    public void Toggle(AnalysisMode mode, bool value)
    {
        switch (mode)
        {
            case AnalysisMode.Introduction:
                IgnoreIntro = value;
                break;
            case AnalysisMode.Credits:
                IgnoreCredits = value;
                break;
        }
    }

    /// <summary>
    /// Checks if the provided mode is ignored.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>True if ignored, false otherwise.</returns>
    public bool IsIgnored(AnalysisMode mode)
    {
        return mode switch
        {
            AnalysisMode.Introduction => IgnoreIntro,
            AnalysisMode.Credits => IgnoreCredits,
            _ => false,
        };
    }
}
