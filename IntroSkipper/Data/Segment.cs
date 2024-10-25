// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace IntroSkipper.Data;

/// <summary>
/// Result of fingerprinting and analyzing two episodes in a season.
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
[DataContract(Namespace = "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper.Segment")]
public class Segment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="segment">Introduction time range.</param>
    public Segment(Guid episode, TimeRange segment)
    {
        EpisodeId = episode;
        Start = segment.Start;
        End = segment.End;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="episode">Episode.</param>
    public Segment(Guid episode)
    {
        EpisodeId = episode;
        Start = 0;
        End = 0;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="intro">intro.</param>
    public Segment(Segment intro)
    {
        EpisodeId = intro.EpisodeId;
        Start = intro.Start;
        End = intro.End;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="intro">intro.</param>
    public Segment(Intro intro)
    {
        EpisodeId = intro.EpisodeId;
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
    [DataMember]
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the introduction sequence start time.
    /// </summary>
    [DataMember]
    public double Start { get; set; }

    /// <summary>
    /// Gets or sets the introduction sequence end time.
    /// </summary>
    [DataMember]
    public double End { get; set; }

    /// <summary>
    /// Gets a value indicating whether this introduction is valid or not.
    /// Invalid results must not be returned through the API.
    /// </summary>
    public bool Valid => End > 0;

    /// <summary>
    /// Gets the duration of this intro.
    /// </summary>
    [JsonIgnore]
    public double Duration => End - Start;

    /// <summary>
    /// Convert this Intro object to a Kodi compatible EDL entry.
    /// </summary>
    /// <param name="action">User specified configuration EDL action.</param>
    /// <returns>String.</returns>
    public string ToEdl(EdlAction action)
    {
        if (action == EdlAction.None)
        {
            throw new ArgumentException("Cannot serialize an EdlAction of None");
        }

        var start = Math.Round(Start, 2);
        var end = Math.Round(End, 2);

        return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", start, end, (int)action);
    }
}
