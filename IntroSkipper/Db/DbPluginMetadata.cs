// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Db;

/// <summary>One durable plugin-owned migration marker.</summary>
public sealed class DbPluginMetadata
{
    /// <summary>Gets or sets the marker key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the marker value.</summary>
    public string Value { get; set; } = string.Empty;
}
