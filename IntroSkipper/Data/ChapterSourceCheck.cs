// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A settled analysis record whose hash names the other chapter source. The season pass reads
/// the episode's chapters from both sources and re-stamps the record from
/// <see cref="StoredHash"/> to <see cref="CurrentHash"/> when they hold the same chapters, or
/// reopens the mode when they do not.
/// </summary>
/// <param name="StoredHash">The hash the record carries, naming the other chapter source.</param>
/// <param name="CurrentHash">The hash the current settings produce for the mode under the record's Chromaprint availability.</param>
/// <param name="Source">The chapter source <see cref="CurrentHash"/> names: the one in effect when the episode was verified.</param>
public readonly record struct ChapterSourceCheck(string StoredHash, string CurrentHash, ChapterSource Source);
