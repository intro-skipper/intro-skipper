// SPDX-FileCopyrightText: 2025-2026 rlauuzo
// SPDX-FileCopyrightText: 2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Describes how a media item in the processing queue should be treated.
/// </summary>
public enum QueuedMediaCategory
{
    /// <summary>
    /// A standard episode that should be analyzed.
    /// </summary>
    Episode,

    /// <summary>
    /// An episode that should be treated with anime-specific rules.
    /// </summary>
    AnimeEpisode,

    /// <summary>
    /// A movie item.
    /// </summary>
    Movie
}
