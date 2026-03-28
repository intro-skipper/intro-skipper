// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

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

}
