using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

/// <summary>
/// Result of fingerprinting and analyzing two episodes in a season.
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
[DataContract(Namespace = "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper")]
public class Intro
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Intro"/> class.
    /// </summary>
    /// <param name="intro">intro.</param>
    public Intro(Segment intro)
    {
        EpisodeId = intro.EpisodeId;
        IntroStart = intro.Start;
        IntroEnd = intro.End;
    }

    /// <summary>
    /// Gets or sets the Episode ID.
    /// </summary>
    [DataMember]
    public Guid EpisodeId { get; set; }

    /// <summary>
    /// Gets a value indicating whether this introduction is valid or not.
    /// Invalid results must not be returned through the API.
    /// </summary>
    public bool Valid => IntroEnd > 0;

    /// <summary>
    /// Gets the duration of this intro.
    /// </summary>
    [JsonIgnore]
    public double Duration => IntroEnd - IntroStart;

    /// <summary>
    /// Gets or sets the introduction sequence start time.
    /// </summary>
    [DataMember]
    public double IntroStart { get; set; }

    /// <summary>
    /// Gets or sets the introduction sequence end time.
    /// </summary>
    [DataMember]
    public double IntroEnd { get; set; }

    /// <summary>
    /// Gets or sets the recommended time to display the skip intro prompt.
    /// </summary>
    public double ShowSkipPromptAt { get; set; }

    /// <summary>
    /// Gets or sets the recommended time to hide the skip intro prompt.
    /// </summary>
    public double HideSkipPromptAt { get; set; }
}
