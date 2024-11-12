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
public class DbSegment : Segment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// </summary>
    /// <param name="segment">The segment to initialize the instance with.</param>
    /// <param name="type">The type of analysis that was used to determine this segment.</param>
    public DbSegment(Segment segment, AnalysisMode type) : base(segment.ItemId, segment.Start, segment.End)
    {
        Type = type;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// </summary>
    public DbSegment()
    {
    }

    /// <summary>
    /// Gets the type of analysis that was used to determine this segment.
    /// </summary>
    public AnalysisMode Type { get; private set; }

    /// <summary>
    /// Converts the instance to a <see cref="Segment"/> object.
    /// </summary>
    /// <returns>A <see cref="Segment"/> object.</returns>
    internal Segment ToSegment()
    {
        return new Segment(ItemId, Start, End);
    }
}
