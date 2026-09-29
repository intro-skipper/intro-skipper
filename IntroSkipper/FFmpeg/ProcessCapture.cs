// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Both output streams and the exit code of a finished process.
/// </summary>
/// <param name="Stdout">Standard output, cut at the byte limit when <paramref name="Truncated"/> is set.</param>
/// <param name="Stderr">Standard error as text, cut at the byte limit when <paramref name="Truncated"/> is set.</param>
/// <param name="ExitCode">The exit code; nonzero when the process failed or was killed.</param>
/// <param name="Truncated">Whether a stream crossed the byte limit and the process was killed for it.</param>
internal sealed record ProcessCapture(ReadOnlyMemory<byte> Stdout, string Stderr, int ExitCode, bool Truncated);
