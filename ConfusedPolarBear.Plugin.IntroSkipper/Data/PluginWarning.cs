using System;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Support bundle warning.
/// </summary>
[Flags]
public enum PluginWarning
{
    /// <summary>
    /// No warnings have been added.
    /// </summary>
    None = 0,

    /// <summary>
    /// Attempted to add skip button to web interface, but was unable to.
    /// </summary>
    UnableToAddSkipButton = 1,

    /// <summary>
    /// At least one media file on the server was unable to be fingerprinted by Chromaprint.
    /// </summary>
    InvalidChromaprintFingerprint = 2,

    /// <summary>
    /// The version of ffmpeg installed on the system is not compatible with the plugin.
    /// </summary>
    IncompatibleFFmpegBuild = 4,
}
