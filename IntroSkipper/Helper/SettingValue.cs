// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Helper;

/// <summary>
/// One plugin setting as reported by <see cref="ConfigurationReport"/>.
/// </summary>
/// <param name="Name">Property name on <see cref="Configuration.PluginConfiguration"/>.</param>
/// <param name="Value">Current value, formatted for display.</param>
/// <param name="Default">Value of a fresh configuration, formatted the same way.</param>
internal sealed record SettingValue(string Name, string Value, string Default)
{
    /// <summary>
    /// Gets a value indicating whether the setting is unchanged from its default.
    /// </summary>
    public bool IsDefault => string.Equals(Value, Default, StringComparison.Ordinal);
}
