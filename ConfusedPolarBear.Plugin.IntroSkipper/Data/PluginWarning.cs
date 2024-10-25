// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace ConfusedPolarBear.Plugin.IntroSkipper;

using System;

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

/// <summary>
/// Warning manager.
/// </summary>
public static class WarningManager
{
    private static PluginWarning warnings;

    /// <summary>
    /// Set warning.
    /// </summary>
    /// <param name="warning">Warning.</param>
    public static void SetFlag(PluginWarning warning)
    {
        warnings |= warning;
    }

    /// <summary>
    /// Clear warnings.
    /// </summary>
    public static void Clear()
    {
        warnings = PluginWarning.None;
    }

    /// <summary>
    /// Get warnings.
    /// </summary>
    /// <returns>Warnings.</returns>
    public static string GetWarnings()
    {
        return warnings.ToString();
    }
}
