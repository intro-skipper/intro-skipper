// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DbSeasonInfo"/> class.
/// </remarks>
public class DbSeasonInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonInfo"/> class.
    /// </summary>
    /// <param name="seasonId">Season ID.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="action">Analyzer action.</param>
    public DbSeasonInfo(Guid seasonId, AnalysisMode mode, AnalyzerAction action)
    {
        SeasonId = seasonId;
        Type = mode;
        Action = action;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSeasonInfo"/> class.
    /// </summary>
    public DbSeasonInfo()
    {
    }

    /// <summary>
    /// Gets the item ID.
    /// </summary>
    public Guid SeasonId { get; private set; }

    /// <summary>
    /// Gets the analysis mode.
    /// </summary>
    public AnalysisMode Type { get; private set; }

    /// <summary>
    /// Gets the analyzer action.
    /// </summary>
    public AnalyzerAction Action { get; private set; }
}
