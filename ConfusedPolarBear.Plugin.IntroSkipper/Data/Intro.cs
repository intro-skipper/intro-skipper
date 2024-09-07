using System;
using System.Runtime.Serialization;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

/// <summary>
/// Result of fingerprinting and analyzing two episodes in a season.
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="Intro"/> class.
/// </remarks>
/// <param name="intro">intro.</param>
[DataContract(Namespace = "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper")]
public class Intro(Segment intro)
{
    /// <summary>
    /// Gets or sets the Episode ID.
    /// </summary>
    [DataMember]
    public Guid EpisodeId { get; set; } = intro.EpisodeId;

    /// <summary>
    /// Gets a value indicating whether this introduction is valid or not.
    /// Invalid results must not be returned through the API.
    /// </summary>
    public bool Valid => IntroEnd > 0;

    /// <summary>
    /// Gets or sets the introduction sequence start time.
    /// </summary>
    [DataMember]
    public double IntroStart { get; set; } = intro.Start;

    /// <summary>
    /// Gets or sets the introduction sequence end time.
    /// </summary>
    [DataMember]
    public double IntroEnd { get; set; } = intro.End;

    /// <summary>
    /// Gets or sets the recommended time to display the skip intro prompt.
    /// </summary>
    public double ShowSkipPromptAt { get; set; }

    /// <summary>
    /// Gets or sets the recommended time to hide the skip intro prompt.
    /// </summary>
    public double HideSkipPromptAt { get; set; }
}
