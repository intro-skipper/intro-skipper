// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// One labelled fact in a <see cref="SupportBundleSection"/>.
/// </summary>
/// <param name="Label">Short label, e.g. "Plugin version".</param>
/// <param name="Value">Single-line value.</param>
public sealed record SupportBundleEntry(string Label, string Value);
