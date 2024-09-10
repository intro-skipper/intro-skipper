namespace ConfusedPolarBear.Plugin.IntroSkipper.Data;

/// <summary>
/// A frame of video that partially (or entirely) consists of black pixels.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="BlackFrame"/> class.
/// </remarks>
/// <param name="percent">Percentage of the frame that is black.</param>
/// <param name="time">Time this frame appears at.</param>
public class BlackFrame(int percent, double time)
{
    /// <summary>
    /// Gets or sets the percentage of the frame that is black.
    /// </summary>
    public int Percentage { get; set; } = percent;

    /// <summary>
    /// Gets or sets the time (in seconds) this frame appeared at.
    /// </summary>
    public double Time { get; set; } = time;
}
