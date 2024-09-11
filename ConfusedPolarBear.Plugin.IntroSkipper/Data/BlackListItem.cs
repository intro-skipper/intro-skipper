using System.Collections.ObjectModel;
using System.Runtime.Serialization;

namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

/// <summary>
/// represents a show that is blacklisted from analysis.
/// </summary>
[DataContract(Namespace = "http://schemas.datacontract.org/2004/07/ConfusedPolarBear.Plugin.IntroSkipper")]
public class BlackListItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BlackListItem"/> class.
    /// </summary>
    /// <param name="series">Show name.</param>
    public BlackListItem(string series)
    {
        Series = series;
    }

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    [DataMember]
    public string Series { get; set; }

    /// <summary>
    /// Gets or sets the blocked analysis modes.
    /// </summary>
    [DataMember]
    public Collection<AnalysisMode> BlackListedModes { get; set; } = new();

    /// <summary>
    /// Updates or adds a blocked mode to the blacklist for this show.
    /// if value is true, the mode is added to the blacklist(if not already present).
    /// if value is false, the mode is removed from the blacklist(if present).
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="value">Value to set.</param>
    public void UpdateOrAddBlackMode(AnalysisMode mode, bool value)
    {
        if (value)
        {
            if (!BlackListedModes.Contains(mode))
            {
                BlackListedModes.Add(mode);
            }
        }
        else
        {
            BlackListedModes.Remove(mode);
        }
    }
}
