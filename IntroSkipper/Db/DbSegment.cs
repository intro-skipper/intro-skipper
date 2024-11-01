// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using IntroSkipper.Data;

namespace IntroSkipper.Db;

/// <summary>
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DbSegment"/> class.
/// </remarks>
public class DbSegment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// </summary>
    /// <param name="segment">Segment.</param>
    /// <param name="mode">Analysis mode.</param>
    public DbSegment(Segment segment, AnalysisMode mode)
    {
        ItemId = segment.EpisodeId;
        Start = segment.Start;
        End = segment.End;
        Type = mode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// </summary>
    public DbSegment()
    {
    }

    /// <summary>
    /// Gets the item ID.
    /// </summary>
    public Guid ItemId { get; private set; }

    /// <summary>
    /// Gets the start time.
    /// </summary>
    public double Start { get; private set; }

    /// <summary>
    /// Gets the end time.
    /// </summary>
    public double End { get; private set; }

    /// <summary>
    /// Gets the analysis mode.
    /// </summary>
    public AnalysisMode Type { get; private set; }
}
