// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities.TV;

namespace IntroSkipper.Helper;

/// <summary>
/// Shared helpers for <see cref="Series"/> metadata queries.
/// </summary>
internal static class SeriesHelper
{
    /// <summary>
    /// Determines whether a series is tagged or categorised as anime.
    /// </summary>
    /// <param name="series">The series to inspect.</param>
    /// <returns><c>true</c> when the series has an "anime" tag or genre; otherwise <c>false</c>.</returns>
    internal static bool IsAnime(Series series) =>
        series.Tags.Contains("anime", StringComparison.OrdinalIgnoreCase) ||
        series.Genres.Contains("anime", StringComparison.OrdinalIgnoreCase);
}
