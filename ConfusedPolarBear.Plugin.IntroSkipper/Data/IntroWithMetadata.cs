namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// An Intro class with episode metadata. Only used in end to end testing programs.
/// </summary>
public class IntroWithMetadata : Intro
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IntroWithMetadata"/> class.
    /// </summary>
    /// <param name="series">Series name.</param>
    /// <param name="season">Season number.</param>
    /// <param name="title">Episode title.</param>
    /// <param name="intro">Intro timestamps.</param>
    public IntroWithMetadata(string series, int season, string title, Intro intro)
    {
        Series = series;
        Season = season;
        Title = title;

        EpisodeId = intro.EpisodeId;
        IntroStart = intro.IntroStart;
        IntroEnd = intro.IntroEnd;
    }

    /// <summary>
    /// Gets or sets the series name of the TV episode associated with this intro.
    /// </summary>
    public string Series { get; set; }

    /// <summary>
    /// Gets or sets the season number of the TV episode associated with this intro.
    /// </summary>
    public int Season { get; set; }

    /// <summary>
    /// Gets or sets the title of the TV episode associated with this intro.
    /// </summary>
    public string Title { get; set; }
}
