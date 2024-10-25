// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

namespace IntroSkipper.Data;

/// <summary>
/// Warning manager.
/// </summary>
public static class WarningManager
{
    private static PluginWarning _warnings;

    /// <summary>
    /// Set warning.
    /// </summary>
    /// <param name="warning">Warning.</param>
    public static void SetFlag(PluginWarning warning)
    {
        _warnings |= warning;
    }

    /// <summary>
    /// Clear warnings.
    /// </summary>
    public static void Clear()
    {
        _warnings = PluginWarning.None;
    }

    /// <summary>
    /// Get warnings.
    /// </summary>
    /// <returns>Warnings.</returns>
    public static string GetWarnings()
    {
        return _warnings.ToString();
    }

    /// <summary>
    /// Check if a specific warning flag is set.
    /// </summary>
    /// <param name="warning">Warning flag to check.</param>
    /// <returns>True if the flag is set, otherwise false.</returns>
    public static bool HasFlag(PluginWarning warning)
    {
        return (_warnings & warning) == warning;
    }
}
