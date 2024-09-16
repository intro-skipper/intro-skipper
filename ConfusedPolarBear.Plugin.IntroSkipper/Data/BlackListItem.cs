using System;
using System.Runtime.Serialization;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

/// <summary>
/// represents a season to be ignored.
/// </summary>
[DataContract(Namespace = "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper")]
public class BlackListItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BlackListItem"/> class.
    /// </summary>
    public BlackListItem()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BlackListItem"/> class.
    /// </summary>
    /// <param name="id">The season id.</param>
    public BlackListItem(Guid id)
    {
        Id = id;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BlackListItem"/> class.
    /// </summary>
    /// <param name="item">The item to copy.</param>
    public BlackListItem(BlackListItem item)
    {
        Id = item.Id;
        IgnoreIntro = item.IgnoreIntro;
        IgnoreCredits = item.IgnoreCredits;
    }

    /// <summary>
    /// Gets or sets the season id.
    /// </summary>
    [DataMember]
    public Guid Id { get; set; } = Guid.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to ignore the intro.
    /// </summary>
    [DataMember]
    public bool IgnoreIntro { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to ignore the credits.
    /// </summary>
    [DataMember]
    public bool IgnoreCredits { get; set; } = false;

    /// <summary>
    /// Updates or adds a blocked mode to the blacklist for this show.
    /// if value is true, the mode is added to the blacklist(if not already present).
    /// if value is false, the mode is removed from the blacklist(if present).
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="value">Value to set.</param>
    public void Toggle(AnalysisMode mode, bool value)
    {
        switch (mode)
        {
            case AnalysisMode.Introduction:
                IgnoreIntro = value;
                break;
            case AnalysisMode.Credits:
                IgnoreCredits = value;
                break;
        }
    }

    /// <summary>
    /// Checks if the provided mode is ignored for this show.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns>True if the mode is ignored, false otherwise.</returns>
    public bool IsIgnored(AnalysisMode mode)
    {
        return mode switch
        {
            AnalysisMode.Introduction => IgnoreIntro,
            AnalysisMode.Credits => IgnoreCredits,
            _ => false,
        };
    }
}
