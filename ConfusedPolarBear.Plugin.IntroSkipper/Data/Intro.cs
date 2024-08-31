using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

/// <summary>
/// Result of fingerprinting and analyzing two episodes in a season.
/// All times are measured in seconds relative to the beginning of the media file.
/// </summary>
public class Intro
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Intro"/> class.
    /// </summary>
    /// <param name="episode">Episode.</param>
    /// <param name="intro">Introduction time range.</param>
    public Intro(Guid episode, TimeRange intro)
    {
        EpisodeId = episode;
        IntroStart = intro.Start;
        IntroEnd = intro.End;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Intro"/> class.
    /// </summary>
    /// <param name="episode">Episode.</param>
    public Intro(Guid episode)
    {
        EpisodeId = episode;
        IntroStart = 0;
        IntroEnd = 0;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Intro"/> class.
    /// </summary>
    /// <param name="intro">intro.</param>
    public Intro(Intro intro)
    {
        EpisodeId = intro.EpisodeId;
        IntroStart = intro.IntroStart;
        IntroEnd = intro.IntroEnd;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Intro"/> class.
    /// </summary>
    public Intro()
    {
    }

    /// <summary>
    /// Gets or sets the Episode ID.
    /// </summary>
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
    public double IntroStart { get; set; }

    /// <summary>
    /// Gets or sets the introduction sequence end time.
    /// </summary>
    public double IntroEnd { get; set; }

    /// <summary>
    /// Gets or sets the recommended time to display the skip intro prompt.
    /// </summary>
    public double ShowSkipPromptAt { get; set; }

    /// <summary>
    /// Gets or sets the recommended time to hide the skip intro prompt.
    /// </summary>
    public double HideSkipPromptAt { get; set; }

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

        var start = Math.Round(IntroStart, 2);
        var end = Math.Round(IntroEnd, 2);

        return string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", start, end, (int)action);
    }
}
