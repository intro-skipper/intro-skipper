// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// The text subtitle cues read from one episode's embedded streams and adjacent sidecars.
/// </summary>
/// <param name="Cues">Cues of the sources that could be read, in media seconds and ordered by start time; empty when none could be read.</param>
/// <param name="Complete">Whether the stream probe and every source were read. When <see langword="false"/>, a matching cue is still usable, but no match does not show the episode has none.</param>
public sealed record SubtitleScan(IReadOnlyList<SubtitleCue> Cues, bool Complete);
