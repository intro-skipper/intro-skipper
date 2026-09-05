// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Reflection;
using System.Xml.Serialization;
using IntroSkipper.Configuration;

namespace IntroSkipper.Helper;

/// <summary>
/// Lists every persisted plugin setting with its current and default value for the support bundle,
/// so newly added settings show up without any bookkeeping here.
/// </summary>
internal static class ConfigurationReport
{
    // Public readable instance properties are the persisted settings; [XmlIgnore] members are runtime state.
    private static readonly PropertyInfo[] SettingProperties =
    [
        .. typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(XmlIgnoreAttribute)))
            .OrderBy(p => p.MetadataToken),
    ];

    /// <summary>
    /// Enumerates the settings of <paramref name="config"/> in declaration order, each paired with the value
    /// a fresh <see cref="PluginConfiguration"/> would have.
    /// </summary>
    /// <param name="config">Configuration to report on.</param>
    /// <returns>One entry per setting.</returns>
    public static IReadOnlyList<SettingValue> Enumerate(PluginConfiguration config)
    {
        var defaults = new PluginConfiguration();
        return [.. SettingProperties.Select(p => new SettingValue(p.Name, Format(p.GetValue(config)), Format(p.GetValue(defaults))))];
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        string text => text.Length == 0 ? "(empty)" : text,
        IEnumerable<string> list => "[" + string.Join(", ", list) + "]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
