// Copyright (C) 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

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
    /// <param name="segment">The segment to initialize the instance with.</param>
    /// <param name="type">The type of analysis that was used to determine this segment.</param>
    /// <param name="isFirstAppearance">Whether this is the first episode where this intro pattern was detected.</param>
    public DbSegment(Segment segment, AnalysisMode type, bool isFirstAppearance = false)
    {
        ItemId = segment.EpisodeId;
        SeasonId = segment.SeasonId;
        Start = segment.Start;
        End = segment.End;
        Type = type;
        IsFirstAppearance = isFirstAppearance;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DbSegment"/> class.
    /// </summary>
    public DbSegment()
    {
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets or sets the primary key.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the episode id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the season id.
    /// </summary>
    public Guid SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the start time.
    /// </summary>
    public double Start { get; set; }

    /// <summary>
    /// Gets or sets the end time.
    /// </summary>
    public double End { get; set; }

    /// <summary>
    /// Gets or sets the type of analysis that was used to determine this segment.
    /// </summary>
    public AnalysisMode Type { get; set; }

    /// <summary>
    /// Gets or sets when this segment was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when this segment was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is the first episode where this intro pattern was detected.
    /// When true, this segment represents the origin of an intro pattern in the season's analysis sequence.
    /// </summary>
    public bool IsFirstAppearance { get; set; }

    /// <summary>
    /// Gets a value indicating whether this segment is valid.
    /// </summary>
    public bool Valid => End > 0.0;

    /// <summary>
    /// Converts the instance to a <see cref="Segment"/> object.
    /// </summary>
    /// <returns>A <see cref="Segment"/> object.</returns>
    public Segment ToSegment()
    {
        return new Segment(ItemId, new TimeRange(Start, End), SeasonId);
    }
}
