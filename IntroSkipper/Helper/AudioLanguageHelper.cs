// SPDX-FileCopyrightText: 2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Helper;

/// <summary>
/// Normalization for the configured preferred audio language so stream selection
/// and configuration hashing always agree on how the value is interpreted.
/// </summary>
public static class AudioLanguageHelper
{
    /// <summary>
    /// Normalizes a configured audio language code by trimming whitespace and lower-casing it.
    /// </summary>
    /// <param name="language">Configured language code.</param>
    /// <returns>The normalized language code, or an empty string when unset.</returns>
    public static string Normalize(string? language) => language?.Trim().ToLowerInvariant() ?? string.Empty;
}
