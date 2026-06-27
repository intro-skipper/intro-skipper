// SPDX-FileCopyrightText: 2026 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;

namespace IntroSkipper.Evaluation;

/// <summary>
/// Builds the normalized key used to join ground-truth labels to detections.
/// The series name is upper-cased invariantly and trimmed so trivial casing or
/// whitespace differences between a label file and a detection export still match.
/// </summary>
internal static class RecapEpisodeKey
{
    /// <summary>
    /// Builds a stable join key from the series name, season number and episode number.
    /// </summary>
    /// <param name="series">Series name.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>A normalized, case-insensitive key.</returns>
    public static string For(string? series, int season, int episode)
    {
        var normalizedSeries = (series ?? string.Empty).Trim().ToUpperInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"{normalizedSeries}|S{season}|E{episode}");
    }
}
