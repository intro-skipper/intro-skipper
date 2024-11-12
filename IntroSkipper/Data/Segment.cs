// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;

namespace IntroSkipper.Data;

/// <summary>
/// Result of fingerprinting and analyzing two episodes in a season.
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
public class Segment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="segment">Introduction time range.</param>
    public Segment(Guid episode, TimeRange segment)
    {
        ItemId = episode;
        Start = segment.Start;
        End = segment.End;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="start">Start time.</param>
    /// <param name="end">End time.</param>
    public Segment(Guid episode, double start = 0.0, double end = 0.0)
    {
        ItemId = episode;
        Start = start;
        End = end;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="intro">intro.</param>
    public Segment(Segment intro)
    {
        ItemId = intro.ItemId;
        Start = intro.Start;
        End = intro.End;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="intro">intro.</param>
    public Segment(Intro intro)
    {
        ItemId = intro.EpisodeId;
        Start = intro.IntroStart;
        End = intro.IntroEnd;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    public Segment()
    {
    }

    /// <summary>
    /// Gets or sets the Episode ID.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the introduction sequence start time.
    /// </summary>
    public double Start { get; set; }

    /// <summary>
    /// Gets or sets the introduction sequence end time.
    /// </summary>
    public double End { get; set; }

    /// <summary>
    /// Gets a value indicating whether this introduction is valid or not.
    /// Invalid results must not be returned through the API.
    /// </summary>
    public bool Valid => End > 0.0;

    /// <summary>
    /// Gets the duration of this intro.
    /// </summary>
    public double Duration => End - Start;
}
