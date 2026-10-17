// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Where analysis reads an item's chapters: Jellyfin's chapter table, or the chapters embedded
/// in the media file, which leave out chapters another plugin added to the table
/// (<c>PluginConfiguration.UseEmbeddedChaptersForAnalysis</c>).
/// </summary>
public enum ChapterSource
{
    /// <summary>
    /// Jellyfin's chapter table.
    /// </summary>
    JellyfinTable,

    /// <summary>
    /// The chapters embedded in the media file, read with ffprobe as Jellyfin's probe stores them.
    /// </summary>
    Embedded,
}
