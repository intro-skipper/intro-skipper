// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// A timed text cue extracted from a media subtitle stream.
/// </summary>
/// <param name="Start">Cue start in media seconds.</param>
/// <param name="End">Cue end in media seconds.</param>
/// <param name="Text">Cue text, with subtitle formatting still permitted.</param>
public sealed record SubtitleCue(double Start, double End, string Text);
