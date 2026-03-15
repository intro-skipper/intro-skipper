// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System;
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
        Start = 0.0;
        End = 0.0;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Segment"/> class.
    /// </summary>
    /// <param name="segment">Segment to copy.</param>
    public Segment(Segment segment)
    {
        EpisodeId = segment.EpisodeId;
        Start = segment.Start;
        End = segment.End;
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
    /// Gets a value indicating whether this segment is valid or not.
    /// Invalid results must not be returned through the API.
    /// </summary>
    public bool Valid => End > 0.0;

    /// <summary>
    /// Gets the duration of this segment.
    /// </summary>
    [JsonIgnore]
    public double Duration => End - Start;
}
